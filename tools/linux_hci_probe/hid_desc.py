#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""IPRC1000 · HID 报告描述符解析

回答一个具体问题：**这条 HID 通道能不能往遥控器写东西**（固件/配置）。
  · Input  = 设备→主机（按键）
  · Output = 主机→设备（可写，但要被固件消费）
  · Feature = 主机→设备的配置通道（Set_Report / Get_Report）

用法： sudo python3 hid_desc.py [report_descriptor 路径]
不给路径就自动找 HID_NAME=IPRC1000 的那个 hidraw。
"""
import glob
import os
import sys

ITEM_TYPE = {0: "Main", 1: "Global", 2: "Local", 3: "Reserved"}
GLOBAL_TAGS = {0: "UsagePage", 1: "LogicalMin", 2: "LogicalMax", 3: "PhysicalMin",
               4: "PhysicalMax", 5: "UnitExp", 6: "Unit", 7: "ReportSize",
               8: "ReportID", 9: "ReportCount", 10: "Push", 11: "Pop"}
LOCAL_TAGS = {0: "Usage", 1: "UsageMin", 2: "UsageMax", 3: "DesignatorIndex",
              4: "DesignatorMin", 5: "DesignatorMax", 7: "StringIndex",
              8: "StringMin", 9: "StringMax", 10: "Delimiter"}
MAIN_TAGS = {8: "Input", 9: "Output", 10: "Collection", 11: "Feature", 12: "EndCollection"}
USAGE_PAGE = {0x01: "Generic Desktop", 0x02: "Simulation", 0x06: "Generic Device",
              0x07: "Keyboard/Keypad", 0x08: "LED", 0x09: "Button", 0x0C: "Consumer",
              0x0B: "Telephony", 0xFF00: "Vendor 0xFF00", 0xFF01: "Vendor 0xFF01",
              0xFFA0: "Vendor 0xFFA0"}
COLLECTION = {0: "Physical", 1: "Application", 2: "Logical", 3: "Report",
              4: "NamedArray", 5: "UsageSwitch", 6: "UsageModifier"}


def find_desc():
    for h in sorted(glob.glob("/sys/class/hidraw/hidraw*")):
        uev = h + "/device/uevent"
        try:
            txt = open(uev).read()
        except OSError:
            continue
        if "IPRC1000" in txt:
            return h + "/device/report_descriptor"
    return None


def parse(data):
    i, g, out = 0, {}, []
    g = {"usage_page": 0, "report_id": 0, "report_size": 0, "report_count": 0,
         "logical_min": 0, "logical_max": 0}
    depth = 0
    while i < len(data):
        b = data[i]; i += 1
        if b == 0xFE:                      # long item
            size = data[i]; i += 2 + size
            continue
        size = (0, 1, 2, 4)[b & 3]
        typ = (b >> 2) & 3
        tag = b >> 4
        val = int.from_bytes(data[i:i + size], "little") if size else 0
        i += size

        if typ == 1:                       # Global
            name = GLOBAL_TAGS.get(tag, f"G{tag}")
            if name == "UsagePage":   g["usage_page"] = val
            elif name == "ReportSize":  g["report_size"] = val
            elif name == "ReportCount": g["report_count"] = val
            elif name == "ReportID":    g["report_id"] = val
            elif name == "LogicalMin":  g["logical_min"] = val
            elif name == "LogicalMax":  g["logical_max"] = val
            out.append(("  " * depth + f"    {name} = 0x{val:X}"))
        elif typ == 0:                     # Main
            name = MAIN_TAGS.get(tag, f"M{tag}")
            if name == "Collection":
                cp = USAGE_PAGE.get(g["usage_page"], f"0x{g['usage_page']:04X}")
                out.append("  " * depth + f"[Collection] {cp} / "
                           + COLLECTION.get(val, f"0x{val:X}"))
                depth += 1
            elif name == "EndCollection":
                depth = max(0, depth - 1)
            elif name in ("Input", "Output", "Feature"):
                bits = g["report_size"] * g["report_count"]
                out.append("  " * depth
                           + f"{name:8s} id={g['report_id']} page={USAGE_PAGE.get(g['usage_page'], hex(g['usage_page']))}"
                           + f"  {g['report_count']}x{g['report_size']}bit = {bits // 8} B"
                           + f"  logical {g['logical_min']}..{g['logical_max']}")
        # Local items: 忽略（对结论无影响）
    return out


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else find_desc()
    if not path or not os.path.exists(path):
        sys.exit("找不到 report_descriptor（遥控器连上了吗？）")
    data = open(path, "rb").read()
    print(f"descriptor : {path}")
    print(f"length     : {len(data)} bytes\n")
    print("raw hex:")
    for k in range(0, len(data), 16):
        print("  " + " ".join(f"{b:02x}" for b in data[k:k + 16]))
    print("\nparsed:")
    for ln in parse(data):
        print("  " + ln)

    # 结论
    n_out = sum(1 for ln in parse(data) if ln.strip().startswith("Output"))
    n_feat = sum(1 for ln in parse(data) if ln.strip().startswith("Feature"))
    n_in = sum(1 for ln in parse(data) if ln.strip().startswith("Input"))
    print("\n=== 结论 ===")
    print(f"  Input 报告 : {n_in}")
    print(f"  Output 报告: {n_out}     <- 主机可写，但要固件消费")
    print(f"  Feature 报告: {n_feat}   <- 唯一标准的'配置通道'")
    if n_feat == 0 and n_out == 0:
        print("  => 这条 HID 通道**没有任何写入路径**：只能读按键，不能下发任何东西。")
        print("     （与 Windows 侧 HidP_GetCaps 的 Output/Feature = 0 字节一致）")
    else:
        print("  => 存在写入路径，值得进一步试探。")


if __name__ == "__main__":
    main()
