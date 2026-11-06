# -*- coding: utf-8 -*-
"""
parse_hci.py  —  BTH-BTHPORT ETW (EventID 402) HCI/L2CAP 解析器（开发机/客户机共用唯一版本）

用法:
    python parse_hci.py <xxx.xml>            # 正常输出
    python parse_hci.py <xxx.xml> --brief    # 只要时间线

抓包方法（管理员）:
    logman start btgrab -p Microsoft-Windows-BTH-BTHPORT 0xffffffffffffffff 5 -o cap.etl -ets
    ... 复现故障 ...
    logman stop btgrab -ets
    tracerpt cap.etl -of XML -o cap.xml -y

字段偏移说明（v2 修正，此前版本有 3 处 off-by-one，导致两边结论对不上）:
    HCI 事件  : [0]=code [1]=plen [2:]=params
    ACL 数据  : [0..1]=handle [2..3]=dlen [4:]=payload
    L2CAP     : [0..1]=len [2..3]=cid | 信令: [4]=code [5]=ident [6..7]=slen [8:]=data
    Disconnect Complete : reason = params[3] => b[5]
    Connection Request  : bdaddr = b[2:8] 反转
"""
import sys
import collections
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

HCI_EVENTS = {
    0x01: 'Inquiry Complete', 0x02: 'Inquiry Result', 0x03: 'Connection Complete',
    0x04: 'Connection Request', 0x05: 'Disconnection Complete', 0x06: 'Auth Complete',
    0x07: 'Remote Name Req Complete', 0x08: 'Encryption Change',
    0x09: 'Change Conn Link Key Complete', 0x0B: 'Read Remote Supported Feat Complete',
    0x0C: 'Read Remote Version Complete', 0x0E: 'Command Complete',
    0x0F: 'Command Status', 0x13: 'Num Completed Packets', 0x1B: 'Max Slots Change',
    0x1C: 'Read Clock Offset Complete', 0x20: 'Page Scan Rep Mode Change',
    0x22: 'Inquiry Result with RSSI', 0x2F: 'Extended Inquiry Result', 0x3E: 'LE Meta',
}

HCI_CMD = {
    0x0405: 'Create Connection', 0x0406: 'Disconnect', 0x0409: 'Accept Connection Request',
    0x040A: 'Reject Connection Request', 0x040B: 'Create Connection Cancel',
    0x0401: 'Inquiry', 0x0402: 'Inquiry Cancel', 0x0408: 'Authentication Requested',
    0x040D: 'Remote Name Request', 0x0411: 'Read Remote Supported Features',
    0x041B: 'Read Remote Extended Features', 0x041D: 'Read Remote Version Information',
    0x0428: 'Setup Synchronous Connection', 0x0429: 'Accept Synchronous Connection',
    0x0C03: 'Reset', 0x0C01: 'Set Event Mask', 0x1005: 'Read Buffer Size',
    0x0C33: 'Host Buffer Size', 0x2002: 'Inquiry Scan Enable', 0x200C: 'Write Scan Enable',
    0x2016: 'Write Local Name', 0x2013: 'Write Class of Device',
    0x1009: 'Write Link Supervision Timeout', 0x1007: 'Write Page Timeout',
}

L2_SIG = {
    0x01: 'Command Reject', 0x02: 'Connection Request', 0x03: 'Connection Response',
    0x04: 'Configure Request', 0x05: 'Configure Response', 0x06: 'Disconnection Request',
    0x07: 'Disconnection Response', 0x08: 'Echo Request', 0x09: 'Echo Response',
    0x0A: 'Information Request', 0x0B: 'Information Response',
    0x0C: 'Create Channel Request', 0x0D: 'Create Channel Response',
}

CONN_RESULT = {0: 'SUCCESS', 1: 'PENDING', 2: 'REFUSED_PSM',
               3: 'REFUSED_SECURITY', 4: 'REFUSED_RESOURCES'}

DISC_REASON = {0x01: 'unknown', 0x05: 'AUTH_FAILURE', 0x08: 'CONNECTION_TIMEOUT',
               0x13: 'REMOTE_USER_TERMINATED', 0x14: 'LOCAL_USER_TERMINATED',
               0x16: 'LOCAL_HOST_TERMINATED', 0x3B: 'UNACCEPTABLE_CONN_PARAMS'}

# BIP_Type 方向（已用"Windows 在无 profile 注册时回 PENDING→REFUSED_PSM"行为校准）
DIR = {3: '<= 入向(遥控器→主机)', 4: '=> 出向(主机→遥控器)'}


def psm_name(p):
    return {0x0001: 'SDP', 0x0003: 'RFCOMM', 0x000F: 'BNEP', 0x0011: 'HID-Control',
            0x0013: 'HID-Interrupt', 0x0017: 'AVCTP', 0x0019: 'AVDTP'}.get(p, '0x%04X' % p)


def bdaddr(bs):
    return ':'.join('%02X' % x for x in reversed(bs))


def classify(b):
    n = len(b)
    if n >= 2 and b[0] in HCI_EVENTS and 2 + b[1] == n:
        return 'evt'
    if n >= 3 and 3 + b[2] == n and ((b[0] | (b[1] << 8)) >> 10) & 0x3F in (1, 2, 3, 4, 5, 6, 8):
        return 'cmd'
    if n >= 4 and 4 + (b[2] | (b[3] << 8)) == n:
        return 'acl'
    return '?'


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    path = sys.argv[1]
    brief = '--brief' in sys.argv

    raw = None
    for enc in ('utf-8-sig', 'utf-16', 'gbk', 'utf-8'):
        try:
            raw = open(path, 'r', encoding=enc).read()
            break
        except Exception:
            continue
    if raw is None:
        raise SystemExit('无法读取 XML')

    raw = raw.replace('xmlns="http://schemas.microsoft.com/win/2004/08/events/event"', '')
    try:
        root = ET.fromstring(raw)
    except ET.ParseError:
        root = ET.fromstring(raw.rstrip('\x00').split('<Event')[0] + '<Events>' + ''.join(
            '<Event' + s for s in raw.split('<Event')[1:]) + '</Events>')

    events = []
    for ev in root.iter('Event'):
        sid = ev.find('.//EventID')
        if sid is None or sid.text != '402':
            continue
        tc = ev.find('.//TimeCreated')
        t = None
        if tc is not None:
            try:
                t = datetime.fromisoformat(tc.get('SystemTime').rstrip('Z')[:26]).replace(tzinfo=timezone.utc)
            except Exception:
                pass
        d = {x.get('Name'): x.text for x in ev.iter('Data')}
        events.append((t, d))

    print('HCI 事件总数: %d' % len(events))
    if not events:
        return
    ts = [t for t, _ in events if t]
    if ts:
        print('时间范围: %s ~ %s' % (min(ts).astimezone().strftime('%H:%M:%S.%f')[:-3],
                                     max(ts).astimezone().strftime('%H:%M:%S.%f')[:-3]))

    kinds, evt_c, cmd_c, cid_c, sig_c, psm_c = (collections.Counter() for _ in range(6))
    timeline = []

    for t, d in events:
        hx = (d.get('BIP_Data') or '').strip()
        if hx.lower().startswith('0x'):
            hx = hx[2:]
        hx = ''.join(c for c in hx if c in '0123456789abcdefABCDEF')
        if len(hx) % 2:
            hx = hx[:-1]
        if not hx:
            continue
        b = bytes.fromhex(hx)
        k = classify(b)
        kinds[k] += 1
        hh = t.astimezone().strftime('%H:%M:%S.%f')[:-3] if t else '??'

        if k == 'evt':
            code = b[0]
            evt_c[HCI_EVENTS.get(code, '0x%02X' % code)] += 1
            if code == 0x04 and len(b) >= 12:
                timeline.append('[%s] 遥控器 Connection Request  from %s  COD=0x%06X' %
                                (hh, bdaddr(b[2:8]), b[8] | (b[9] << 8) | (b[10] << 16)))
            elif code == 0x03 and len(b) >= 13:
                st = b[2]
                timeline.append('[%s] Connection Complete   status=0x%02X %s  handle=0x%04X  %s' %
                                (hh, st, 'OK' if st == 0 else 'FAIL', b[3] | (b[4] << 8), bdaddr(b[5:11])))
            elif code == 0x08 and len(b) >= 5:
                timeline.append('[%s] Encryption Change     status=0x%02X enable=%d' % (hh, b[2], b[3]))
            elif code == 0x06 and len(b) >= 5:
                timeline.append('[%s] Auth Complete         status=0x%02X' % (hh, b[2]))
            elif code == 0x05 and len(b) >= 6:
                r = b[5]
                timeline.append('[%s] Disconnection Complete reason=0x%02X %s' %
                                (hh, r, DISC_REASON.get(r, '')))
        elif k == 'cmd':
            op = b[0] | (b[1] << 8)
            nm = HCI_CMD.get(op, '0x%04X' % op)
            cmd_c[nm] += 1
            if nm in ('Create Connection', 'Accept Connection Request', 'Reject Connection Request',
                      'Remote Name Request', 'Authentication Requested'):
                timeline.append('[%s] HCI-CMD %s' % (hh, nm))
        elif k == 'acl':
            pdu = b[4:]
            if len(pdu) < 4:
                continue
            cid = pdu[2] | (pdu[3] << 8)
            cid_c['0x%04X' % cid] += 1
            if cid == 0x0001 and len(pdu) >= 8:
                code, ident = pdu[4], pdu[5]
                slen = pdu[6] | (pdu[7] << 8)
                sd = pdu[8:8 + slen]
                sig_c[L2_SIG.get(code, '0x%02X' % code)] += 1
                if code == 0x02 and len(sd) >= 4:
                    psm = sd[0] | (sd[1] << 8)
                    psm_c['%s(0x%04X)' % (psm_name(psm), psm)] += 1
                    timeline.append('[%s]   L2CAP ConnReq   PSM=%s SCID=0x%04X ident=0x%02X' %
                                    (hh, psm_name(psm), sd[2] | (sd[3] << 8), ident))
                elif code == 0x03 and len(sd) >= 8:
                    res = sd[4] | (sd[5] << 8)
                    timeline.append('[%s]   L2CAP ConnRsp   DCID=0x%04X SCID=0x%04X ident=0x%02X  >> %s' %
                                    (hh, sd[0] | (sd[1] << 8), sd[2] | (sd[3] << 8), ident,
                                     CONN_RESULT.get(res, res)))

    print('\n=== 时间线 ===')
    for ln in timeline:
        print('  ' + ln)
    if brief:
        return

    print('\n=== 包类型 ===')
    for k, v in kinds.items():
        print('  %-5s %d' % (k, v))
    print('\n=== HCI 事件 ===')
    for k, v in evt_c.most_common():
        print('  %-40s %d' % (k, v))
    print('\n=== HCI 命令 ===')
    for k, v in cmd_c.most_common():
        print('  %-40s %d' % (k, v))
    print('\n=== L2CAP CID ===')
    for k, v in cid_c.most_common():
        print('  CID %s %-14s %d' % (k, {'0x0001': 'Signaling', '0x0004': 'SDP',
                                          '0x0011': 'HID-Control', '0x0013': 'HID-Interrupt',
                                          '0x0040': 'ATT'}.get(k, ''), v))
    print('\n=== L2CAP 信令 ===')
    for k, v in sig_c.most_common():
        print('  %-30s %d' % (k, v))
    print('\n=== ConnReq 的 PSM ===')
    if psm_c:
        for k, v in psm_c.most_common():
            print('  %-24s %d' % (k, v))
    else:
        print('  (无 Connection Request)')


if __name__ == '__main__':
    main()
