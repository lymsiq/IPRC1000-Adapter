// ───────── 屏幕键盘（Windows 全尺寸）与配置组 ─────────
// 每一项 [显示文字, 虚拟键码, 宽度类]
const KB_ROWS = [
  [['Esc',0x1B,'w15'],['F1',0x70],['F2',0x71],['F3',0x72],['F4',0x73],['F5',0x74],['F6',0x75],
   ['F7',0x76],['F8',0x77],['F9',0x78],['F10',0x79],['F11',0x7A],['F12',0x7B]],
  [['`',0xC0],['1',0x31],['2',0x32],['3',0x33],['4',0x34],['5',0x35],['6',0x36],['7',0x37],
   ['8',0x38],['9',0x39],['0',0x30],['-',0xBD],['=',0xBB],['⌫',0x08,'w2']],
  [['Tab',0x09,'w15'],['Q',0x51],['W',0x57],['E',0x45],['R',0x52],['T',0x54],['Y',0x59],
   ['U',0x55],['I',0x49],['O',0x4F],['P',0x50],['[',0xDB],[']',0xDD],['\\',0xDC,'w15']],
  [['Caps',0x14,'w2'],['A',0x41],['S',0x53],['D',0x44],['F',0x46],['G',0x47],['H',0x48],
   ['J',0x4A],['K',0x4B],['L',0x4C],[';',0xBA],["'",0xDE],['enter',0x0D,'w25']],
  [['Shift',0x10,'w25'],['Z',0x5A],['X',0x58],['C',0x43],['V',0x56],['B',0x42],['N',0x4E],
   ['M',0x4D],[',',0xBC],['.',0xBE],['/',0xBF],['Shift',0x10,'w25']],
  // 底部行总宽与上面各行对齐（1.5宽Ctrl + 6宽Space + 1.5宽Ctrl 等 ≈ 15 个单位）
  [['Ctrl',0x11,'w15'],['Win',0x5B],['Alt',0x12],['Space',0x20,'w7'],
   ['Alt',0x12],['Win',0x5B],['Menu',0x5D],['Ctrl',0x11,'w15']],
];
// 修饰键用 **Windows 叫法**（Ctrl / Alt / Shift / Win 开始键），不用 mac 那套
const KB_MODS  = [['Ctrl',0x11],['Alt',0x12],['Shift',0x10],['Win（开始键）',0x5B]];
// 媒体键 + 常用导航键（Esc / Space 已在主键盘里，这里不再重复，避免冗余）
const KB_EXTRA = [['播放/暂停',0xB3],['上一首',0xB1],['下一首',0xB0],['音量 −',0xAE],['音量 +',0xAF],['静音',0xAD],
                  ['↑',0x26],['↓',0x28],['←',0x25],['→',0x27],['PgUp',0x21],['PgDn',0x22]];
const VK_SYM = {0x11:'Ctrl',0x12:'Alt',0x10:'Shift',0x5B:'Win'};

/* ── 动作值：映射值用**负数哨兵**（与 C# 侧 KeyAct.cs 一一对应）──
   为什么用负数：虚拟键码（VK）是 0x00..0xFF，负数**永远不可能**是真实按键，
   万一哪里漏了分派，它会立刻变成明确的越界值，而不是悄悄注入一个错的键盘键。
   ★ 「切换配置组」(-5) 是**方块键的出厂值** —— v1.2.4 起方块键不再是硬编码的特殊键，
     它就是个普通键，所以这里和鼠标动作放在同一行里供选择。 */
const KEY_ACTS = [['切换配置组',-5],['鼠标左键',-1],['鼠标右键',-2],['滚轮上',-3],['滚轮下',-4],['截图点击',-6]];
const ACT_NAME = Object.fromEntries(KEY_ACTS.map(([n,v]) => [v, n]));
const isKeyAct = (v) => Number.isInteger(v) && v >= -6 && v <= -1;
let kbMods = new Set();    // 已选修饰键
let kbKey = null;          // 已选主键 vk
let kbKeyLabel = '';       // 主键显示名

// ── 全局错误捕获：任何未捕获异常都写进 document.title，便于自检/排查 ──
window.__lastErr = "";
window.addEventListener('error', (e) => {
  window.__lastErr = `${e.message || ''} @行${e.lineno || 0}`;
  try { document.title = 'ERR: ' + window.__lastErr; } catch (_) {}
});
window.addEventListener('unhandledrejection', (e) => {
  window.__lastErr = 'Promise: ' + ((e.reason && e.reason.message) || e.reason);
  try { document.title = 'ERR: ' + window.__lastErr; } catch (_) {}
});

/* IPRC1000 Adapter — 前端逻辑 + QWebChannel 后端桥 */

let B = null;            // backend
let state = null;
let selectedKey = null;
let timer = null;

/**
 * 查询元素；**找不到时返回一个桩对象**而不是 null。
 *
 * 为什么这么写：顶层会有大量 `$('#x').onclick = ...`，一旦某个 id 改名/被删，
 * 原来会抛 `Cannot set properties of null` 并**中断整段脚本**（表现就是整页渲染不出来）。
 * 返回桩对象后，这类问题只会静默失效，不会连累其它页面。
 */
const $ = (sel) => document.querySelector(sel) || new Proxy({}, {
  get: (t, k) => (k in t ? t[k] : (() => {})),
  set: (t, k, v) => { t[k] = v; return true; },
});
const $$ = (s) => [...document.querySelectorAll(s)];

/* ───────── Toast ───────── */
let toastEl;
function toast(msg, ms = 2600) {
  if (!toastEl) {
    toastEl = document.createElement('div');
    toastEl.id = 'toast';
    document.body.appendChild(toastEl);
  }
  toastEl.textContent = msg;
  toastEl.classList.add('show');
  clearTimeout(toastEl._t);
  toastEl._t = setTimeout(() => toastEl.classList.remove('show'), ms);
}

/* ───────── 页面切换 ───────── */
function goto(page) {
  document.body.dataset.page = page;      // 供 CSS 按页做差异（如按键页隐藏左栏重复的遥控器图）
  $$('.nav-btn').forEach((b) => b.classList.toggle('active', b.dataset.page === page));
  $$('.page').forEach((p) => {
    const show = p.id === 'page-' + page;
    p.classList.toggle('hidden', !show);
    if (show) {                       // 只在切到该页时播一次淡入（180ms）
      p.classList.remove('page-entering');
      void p.offsetWidth;             // 强制重排，保证连点两次也能重播
      p.classList.add('page-entering');
      // 播完就摘掉：否则 animation 一直处于 active 状态（曾导致 .page 带着恒等 transform，
      // 使 position:fixed 的弹窗以整页为包含块 —— 弹窗被推到屏幕下方）
      p.addEventListener('animationend', () => p.classList.remove('page-entering'), { once: true });
    } else p.classList.remove('page-entering');
  });
  const m = $('main');
  if (m) m.scrollTop = 0;
  /* ★ 鼠标页的「实时校准区」只在页面可见时轮询。
     本程序是常驻托盘应用 —— 不该在没人看的时候每 50ms 跨进程取一次数据。 */
  if (page === 'mouse') startMousePad(); else stopMousePad();
}

/* ───────── 渲染：功能总览 ───────── */
function setDot(id, cls) {
  const d = $(id);
  if (d) d.className = 'dot' + (cls ? ' ' + cls : '');
}

// ── ③ 第一次使用：四步清单的完成判定 ──
//   判据全部取自现有 state，不新增后端字段：
//   ①配对 ②驱动+声卡 ③默认麦克风是否 CABLE ④是否真的收到过音频
function renderJourney(s) {
  const box = $('#journey');
  if (!box || !s) return;
  const d = s.device || {}, r = s.reg || {}, c = s.cable || {}, b = s.bridge || {};
  const mic = String(c.defaultMicName || '').toLowerCase();
  const done = [
    !!(d.paired || d.connected),
    !!(r.ok && c.ready),
    mic.indexOf('cable') >= 0,
    (b.frames || 0) > 0,
  ];
  const n = done.filter(Boolean).length;
  const prog = $('#journeyProg');
  if (prog) prog.textContent = n + ' / 4 项已确认';
  $$('#journeyList .jstep').forEach((el, i) => el.classList.toggle('done', done[i]));
  if (n === 4 && box.open && box.dataset.autoClosed !== '1') {
    box.open = false; box.dataset.autoClosed = '1';   // 全部完成自动收起，只收一次
  }
}

function renderOverview(s) {
  const d = s.device, k = s.keys, v = s.bridge, r = s.reg, c = s.cable, a = s.admin;

  const conn = d.connected;
  const dt = d.detail || {};
  const hidOn = (dt.hid || []).some((x) => x.online);
  const bthOn = (dt.bthenum || []).some((x) => x.online);
  setDot('#d-device', conn ? 'on' : (d.paired ? 'warn' : 'err'));
  $('#v-device').textContent = conn ? '已连接' : (d.paired ? '已配对 · 休眠中' : '未配对');
  $('#s-device').textContent = conn
    ? `HID 节点在线（${(dt.hid || []).length} 个）· 蓝牙节点在线`
    : `HID 节点${hidOn ? '在线' : '不在线'} · 蓝牙节点${bthOn ? '在线' : '不在线'} · ` +
      `配对${dt.paired ? '已存在' : '缺失'} —— 按遥控器任意键唤醒`;

  setDot('#d-keys', k.configured > 0 ? 'on' : 'warn');
  $('#v-keys').textContent = k.configured + ' 个按键已配置';
  $('#v-keys').nextElementSibling.textContent =
    k.configured + ' / ' + k.total + ' 个按键已设置快捷键';

  // 语音：注册表 → 虚拟声卡 → 桥
  let voiceOk = r.ok && c.ready;
  setDot('#d-voice', voiceOk ? (v.running ? 'on' : 'warn') : 'err');
  $('#v-voice').textContent = !r.ok
    ? '注册表未生效（需重启）'
    : (!c.ready ? '虚拟声卡未安装' : (v.running ? '语音通道已就绪' : '语音通道待启动'));
  $('#s-voice').textContent = !r.ok
    ? 'BthPort ETW 首片被截断，音频不完整'
    : (!c.ready ? '正在等待 VB-CABLE 安装完成' : v.hint);

  const sw = s.swallow || {};
  const ks0 = s.keysource || {};
  const hookMode = ks0.mode === 'hook';      // ★ 本函数内也要定义（之前漏了 → renderOverview 抛异常 → 整页空白）
  const hookOn = !!(ks0.hookActive);
  const stillKb = !!ks0.stillKeyboard;
  // ★ 判据用「钩子是否真的在拦」，不是「捕获器是否在跑」——
  //   否则切成 ETW 但驱动没换时，会误报「拦截已生效」而 m 其实在漏。
  // 无驱动模式：遥控器是键盘，但有钩子在吞 m —— 也算"受保护"
  const driverFreeOn = !!(state && state.driverFree);
  const protectedOk = driverFreeOn || hookOn || !stillKb;
  setDot('#d-perm', protectedOk ? 'on' : 'err');
  $('#v-perm').textContent = protectedOk
    ? (driverFreeOn ? '无驱动模式 · 钩子吞 m'
       : hookOn ? (sw.deviceSeen ? '拦截已生效' : '拦截已就绪 · 等遥控器') : '系统不认它是键盘')
    : '⚠ 未受保护：m 会漏进应用';
  // 概览卡片副标题：按模式给不同信息（ETW 模式下「已拦截/放行」没有意义）
  $('#v-perm').nextElementSibling.textContent = driverFreeOn
    ? `无驱动模式：遥控器保持键盘，它自带的 m 由钩子按连发吞掉（已吞 ${(state&&state.ghostSwallowed)||0} 次）`
    : !protectedOk
    ? `遥控器仍被系统当作<b>键盘</b>（驱动未切换）→ 按键会直接打进窗口（随机冒出字母）` +
      (ks0.guard ? `　【${ks0.guard}】` : '')
    // 2026-10-20：钩子已彻底移除（Start 里只走 ETW），hookMode 恒为 false，
    // 原来那条"已拦截 N 次 / 放行 M 次"的分支**永远不会执行**，且计数恒为 0（假仪表）→ 删掉。
    : `零字符输出 · 按键走 BthPort ETW · 无需拦截`;

  // 反作弊提示（只提示、不改变行为）：本程序用虚拟 HID + SendInput 注入输入
  const ac = $('#acWarn');
  if (ac) {
    ac.innerHTML = s.antiCheat
      ? `检测到 <b>${s.antiCheat}</b> 正在运行。<br>` +
        `本程序用<b>虚拟 HID 键盘</b>注入按键 —— 内核级反作弊可能把它判为违规，` +
        `<b>存在封号风险</b>。建议不要在带反作弊的联机游戏里使用；` +
        `只做语音输入时也请避免在游戏进行中按住语音键。`
      : '';
  }

  $('#opKey').textContent = d.lastKey || '—';
  $('#opState').textContent = d.lastState || '—';

  $('#devHint').textContent = conn ? '已检测：' + (d.lastKey || '已连接') : '已配对 · 休眠中';
  setDot('#linkDot', conn ? 'on' : 'warn');
  $('#linkText').textContent = conn ? '遥控器已连接' : '等待遥控器连接（按任意键唤醒）';

  // 版本 + 构建时间（一眼确认手上跑的是哪一版）
  const rv = $('#railVer');
  if (rv) {
    rv.textContent = s.versionLine || (s.version ? 'v' + s.version : '—');
    rv.title = `程序版本 v${s.version || '?'}` +
               (s.buildStamp ? `\n构建于 ${s.buildStamp}` : '') +
               '\n\n提示：改了按键映射会立即生效；但**换了新的 exe 必须重启程序**。';
  }
}

/* ───────── 渲染：按键映射 ───────── */
// 按键分组（让 23 个键更好找）
const KEY_GROUPS = [
  ['D-PAD', ['up', 'down', 'left', 'right', 'ok', 'back', 'exit', 'menu'], '方向 / 确认 / 返回'],
  ['VOL · MEDIA', ['volume_up', 'volume_down', 'mute', 'play_pause', 'fast_forward', 'rewind'], '音量 / 媒体'],
  ['CH · LAST', ['channel_up', 'channel_down', 'last'], '频道 / 上一频道'],
  ['FUNC', ['voice', 'favorite', 'info', 'account', 'square', 'power'], '语音 / 收藏 / 信息 / 账户 / 方块 / 电源'],
];

const THUMB = 34;          // 行内缩略图边长
let stageScale = 1;        // 遥控器舞台的缩放（用于把原图坐标映射到显示坐标）

// ── 遥控器舞台：铺 23 个热区，选中时高亮 ──
function layoutRemote(items, img) {
  const stage = $('#remoteStage');
  if (!stage) return;
  stage.querySelectorAll('.keyhit').forEach(e => e.remove());
  const iw = (img && img.w) || 179;
  const shown = stage.querySelector('img');
  const shownW = shown ? (shown.clientWidth || 164) : 164;
  stageScale = shownW / iw;
  items.forEach((it) => {
    if (!it.rect) return;
    const { cx, cy, w, h } = it.rect;
    const d = document.createElement('div');
    d.className = 'keyhit';
    d.dataset.id = it.id;
    d.title = `${it.label}  ·  ${it.name}`;
    d.style.left = ((cx - w / 2) * stageScale).toFixed(1) + 'px';
    d.style.top = ((cy - h / 2) * stageScale).toFixed(1) + 'px';
    d.style.width = (w * stageScale).toFixed(1) + 'px';
    d.style.height = (h * stageScale).toFixed(1) + 'px';
    d.onclick = () => { selectKey(it.id); };
    stage.appendChild(d);
  });
  paintRemoteSel();
}

// 只给当前选中的键加高亮
function paintRemoteSel() {
  document.querySelectorAll('#remoteStage .keyhit').forEach((e) => {
    e.classList.toggle('on', e.dataset.id === selectedKey);
  });
}

// 一行：缩略图（sprite 裁自 remote.png）+ 名称 + 组合键 + 箭头
/// 列表右侧那行"这个键当前是什么动作"。
///
/// ★ v1.2.4 起方块键就是**普通可绑定键**（出厂值 = 动作值「切换配置组」），
/// 所以这里不再需要对它特殊照顾 —— 后端给的 `sym` 已经是真实映射。
/// 唯一需要特殊说明的是**鼠标模式组里的四个方向键**：它们由引擎线程接管
/// （按住加速移动），走不了普通映射，显示成"不执行"会让人以为坏了。
const MOUSE_DIRS = ['up', 'down', 'left', 'right'];
function bindSym(it) {
  const g = ((state || {}).groups || {}).items || [];
  const cur = g[((state || {}).groups || {}).active || 0] || {};
  if (cur.kind === 'mouse' && MOUSE_DIRS.includes(it.id)) return '鼠标移动';
  return it.sym || '不执行';
}

function renderKeyRow(list, it) {
  const row = document.createElement('div');
  row.className = 'keyrow' + (it.binding ? '' : ' none') + (selectedKey === it.id ? ' sel' : '');
  row.dataset.id = it.id;
  row.title = `${it.label}  ·  ${it.name}`;

  let thumbStyle = '';
  if (it.rect) {
    const { cx, cy, w, h } = it.rect;
    const img = it.img || { w: 179, h: 663 };
    const scale = THUMB / Math.max(w, h);
    thumbStyle = `background-size:${(img.w * scale).toFixed(1)}px ${(img.h * scale).toFixed(1)}px;` +
                 `background-position:${(THUMB / 2 - cx * scale).toFixed(1)}px ${(THUMB / 2 - cy * scale).toFixed(1)}px;`;
  }
  row.innerHTML = `
    <div class="keythumb" style="${thumbStyle}"></div>
    <div class="keytext">
      <div class="keyname">${it.name}</div>
      <div class="keyhid">HID ${it.hid}</div>
    </div>
    <div class="keybind-sym">${bindSym(it)}</div>
    <div class="chev">›</div>`;
  row.onclick = () => selectKey(it.id);
  list.appendChild(row);
}

function renderKeys(s) {
  const list = $('#keyList');
  if (!list) return;
  const items = (s.keys && s.keys.items) || [];
  const byId = Object.fromEntries(items.map(i => [i.id, i]));
  const configured = items.filter(i => i.binding).length;

  const ks = s.keysource || {};
  const srcName = 'BthPort ETW';   // 只有这一种来源（键盘模式/钩子已移除）
  const sum = $('#keySum');
  if (sum) sum.innerHTML =
    `<span class="pill">按键来源：${srcName}</span>` +
    `<span class="pill">已配置 ${configured}/${items.length}</span>` +
    (s.driverNote ? `<span class="pill">${s.driverNote}</span>` : '') +
    `<span class="pill">点某一行可在下方改快捷键</span>`;

  renderGroups(s);
  layoutRemote(items, (items[0] || {}).img);

  // 搜索过滤：按键名 / 显示标注 / HID 值 / 当前映射动作
  const q = (($('#gSearch') || {}).value || '').trim().toLowerCase();
  const match = (it) => !q ||
    (it.name || '').toLowerCase().includes(q) ||
    (it.label || '').toLowerCase().includes(q) ||
    (it.hid || '').toLowerCase().includes(q) ||
    (it.binding || '').toLowerCase().includes(q) ||
    (it.sym || '').toLowerCase().includes(q);

  list.innerHTML = '';
  const seen = new Set();
  const emit = (it) => { if (it && !seen.has(it.id) && match(it)) { seen.add(it.id); renderKeyRow(list, it); } };
  KEY_GROUPS.forEach(([title, ids, hint]) => {
    const rows = ids.map(id => byId[id]).filter(Boolean);
    if (!rows.length) return;
    const h = document.createElement('div');
    h.className = 'keygroup'; h.textContent = title; if (hint) h.title = hint;
    list.appendChild(h);
    rows.forEach(emit);
  });
  items.forEach(it => { if (!seen.has(it.id)) emit(it); });   // 兜底：未归类的键
}

// ── 配置组 Tab ──
function renderGroups(s) {
  const box = $('#gTabs'); if (!box) return;
  const g = (s && s.groups) || { active: 0, items: [] };
  box.innerHTML = '';
  (g.items || []).forEach((it, i) => {
    const t = document.createElement('div');
    t.className = 'gtab' + (i === g.active ? ' on' : '');
    t.innerHTML = `${it.name}<span class="n">${it.configured}</span>`;
    t.title = '单击切换 · 双击重命名';
    t.onclick = async () => { if (i !== g.active) { await B.switchGroup(String(i)); await refresh(); } };
    t.ondblclick = async () => {
      const nm = prompt('重命名配置组', it.name); if (nm === null) return;
      const r = JSON.parse(await B.renameGroup(String(i), nm)); toast(r.message, 4000); await refresh();
    };
    box.appendChild(t);
  });
}

// ── 屏幕键盘 ──
function kbPreviewText() {
  const parts = [...kbMods].map(v => VK_SYM[v] || String(v));
  if (kbKey !== null) {
    parts.push(isKeyAct(kbKey)
      ? (ACT_NAME[kbKey] || '动作值')                                       // 负数不能当 VK 打印
      : (kbKeyLabel || ('0x' + kbKey.toString(16).toUpperCase())));
  }
  return parts.length ? parts.join(' + ') : '';
}

function paintKb() {
  $('#kbMods').querySelectorAll('.m').forEach((e) => {
    e.classList.toggle('on', kbMods.has(Number(e.dataset.vk)));
  });
  document.querySelectorAll('#kbMain .kb-key, #kbExtra .kb-key').forEach((e) => {
    e.classList.toggle('on', kbKey !== null && Number(e.dataset.vk) === kbKey && !e.dataset.mod);
  });
  // 动作单独高亮：键盘那两行在选中动作时必须全部熄灭，否则会出现两个"选中"
  document.querySelectorAll('#kbMouse .kb-key').forEach((e) => {
    e.classList.toggle('on', kbKey !== null && Number(e.dataset.mouse) === kbKey);
  });
  const txt = kbPreviewText();
  // ★ 两个位置都写：底部「当前：<b>」+ 弹窗头部副标题
  //   （旧版 index.html 里 id="comboCur" 出现过重复，导致只写到其中一个 → 另一个永远是空的）
  const cur = $('#comboCur');
  if (cur) cur.textContent = txt || '不执行（该键不注入）';
  const sub = $('#comboSub');
  if (sub) sub.textContent = '当前：' + (txt || '未设置');
}

// ── 截图点击的全局调参（按键页顶部；原来错误地塞在按键弹窗里）──
function buildClickTune() {
  const box = $('#clickTune');
  if (!box || box.dataset.built) return;
  box.dataset.built = '1';

  const th = document.createElement('label');
  th.style.cssText = 'display:inline-flex;align-items:center;gap:6px;font-size:12.5px;color:#556';
  th.appendChild(document.createTextNode('截图点击 · 匹配门槛'));
  const thIn = document.createElement('input');
  thIn.id = 'clickMinScore';
  thIn.type = 'number'; thIn.step = '0.01'; thIn.min = '0.50'; thIn.max = '0.98';
  thIn.style.cssText = 'width:66px;padding:2px 4px';
  thIn.onchange = async () => {
    const r = JSON.parse(await B.setClickMinScore(thIn.value));
    toast(r.message, 6000);
    thIn.value = String(r.score || thIn.value);
  };
  th.appendChild(thIn);
  th.appendChild(document.createTextNode('（0.75 起步；「试一下」看实际分数再定）'));
  box.appendChild(th);

  const sc = document.createElement('label');
  sc.style.cssText = 'display:inline-flex;align-items:center;gap:6px;font-size:12.5px;color:#556;margin-left:4px';
  sc.appendChild(document.createTextNode('搜索范围'));
  const sel = document.createElement('select');
  sel.id = 'clickScope';
  sel.style.cssText = 'padding:2px 4px';
  [['0', '只搜附近（最快）'], ['1', '窗口下部（默认）'], ['2', '整个窗口（最稳）']].forEach(([v, t]) => {
    const o = document.createElement('option'); o.value = v; o.textContent = t; sel.appendChild(o);
  });
  sel.onchange = async () => {
    const r = JSON.parse(await B.setClickSearchScope(sel.value));
    toast(r.message, 6000);
  };
  sc.appendChild(sel);
  box.appendChild(sc);
}

function buildKeyboard() {
  const mods = $('#kbMods'); if (!mods || mods.dataset.built) return;
  KB_MODS.forEach(([label, vk]) => {
    const d = document.createElement('div');
    d.className = 'm'; d.textContent = label; d.dataset.vk = vk; d.dataset.mod = '1';
    d.onclick = () => { kbMods.has(vk) ? kbMods.delete(vk) : kbMods.add(vk); kbKey = null; kbKeyLabel = ''; paintKb(); };
    mods.appendChild(d);
  });
  const main = $('#kbMain');
  KB_ROWS.forEach((row) => {
    const r = document.createElement('div'); r.className = 'kb-row';
    row.forEach(([label, vk, cls]) => {
      const k = document.createElement('div');
      k.className = 'kb-key ' + (cls || ''); k.textContent = label; k.dataset.vk = vk;
      // 左右修饰键都归一成通用修饰键
      if (label === 'Ctrl' || label === 'Alt' || label === 'Shift' || label === 'Win') { k.dataset.mod = vk; }
      k.onclick = () => { kbKey = vk; kbKeyLabel = label; paintKb(); };
      r.appendChild(k);
    });
    main.appendChild(r);
  });
  const extra = $('#kbExtra');
  KB_EXTRA.forEach(([label, vk]) => {
    const k = document.createElement('div');
    k.className = 'kb-key'; k.style.minWidth = '74px'; k.textContent = label; k.dataset.vk = vk;
    k.onclick = () => { kbKey = vk; kbKeyLabel = label; kbMods.clear(); paintKb(); };
    extra.appendChild(k);
  });
  // 动作行：切换配置组 / 左键 / 右键 / 滚轮上 / 滚轮下
  const mo = $('#kbMouse');
  if (mo) {
    const cap = document.createElement('span');
    cap.className = 'kb-cap';
    cap.textContent = '动作：';
    mo.appendChild(cap);
    KEY_ACTS.forEach(([label, vk]) => {
      const k = document.createElement('div');
      k.className = 'kb-key'; k.style.minWidth = '74px';
      k.textContent = label; k.dataset.mouse = vk;
      // 鼠标动作不能带修饰键（Ctrl+鼠标左键在遥控器上没法表达），所以点它就把修饰键清空
      k.onclick = () => { kbKey = vk; kbKeyLabel = label; kbMods.clear(); paintKb(); };
      mo.appendChild(k);
    });

    // ★ 2026-10-18（用户需求）：把「左键单击」的用法直接写在界面上。
    //   典型场景：豆包的「增强提示词」按钮没有快捷键，必须用鼠标点。
    //   把鼠标停在那个按钮上 → 说话结束后按映射了「鼠标左键」的遥控器键 → 就地单击。
    const hint = document.createElement('div');
    hint.className = 'note';
    hint.style.marginTop = '6px';
    hint.innerHTML =
      '提示：「鼠标左键」= 在<b>光标当前位置</b>单击一次（按住不放 = 拖拽）。<br>' +
      '「<b>截图点击</b>」= 在目标窗口里<b>找到你采集过的图标</b>并点它（窗口移动也跟得住）。' +
      '豆包那个 DirectUI 工具栏不支持无障碍接口，所以只能靠截图找。';
    mo.appendChild(hint);

    // 采集图标：把鼠标停在目标按钮上再点这里
    // ★ 采集必须**倒计时**：点按钮时鼠标在按钮上，而采集取的是"当前光标位置" ——
    //   如果立刻采，采到的永远是按钮自己（真机就这样把自己的窗口采成了目标）。
    //   所以点下后给 3 秒，让用户把鼠标移到目标图标上。
    const learn = document.createElement('button');
    learn.className = 'btn ghost';
    learn.style.marginTop = '6px';
    learn.textContent = '采集图标（点这里，然后把鼠标移到目标按钮上，3 秒后自动采集）';
    let counting = false;
    learn.onclick = async () => {
      if (counting) return;
      counting = true;
      const old = learn.textContent;
      for (let n = 3; n > 0; n--) {
        learn.textContent = `★ ${n} 秒后采集 —— 现在把鼠标移到目标按钮上！`;
        await new Promise((r) => setTimeout(r, 1000));
      }
      learn.textContent = '正在采集…';
      const r = JSON.parse(await B.learnClickIcon());
      learn.textContent = old;
      counting = false;
      toast(r.message, 9000);
    };
    mo.appendChild(learn);

    // 「试一下」：不等遥控器，直接执行一次，立刻看到结果（相似度 / 失败原因）
    const test = document.createElement('button');
    test.className = 'btn ghost';
    test.style.marginLeft = '6px';
    test.textContent = '试一下截图点击';
    test.onclick = async () => {
      toast('正在目标窗口里找图标…', 2000);
      const r = JSON.parse(await B.testClickIcon());
      toast(r.message, 9000);
    };
    mo.appendChild(test);

    // 2026-10-27：匹配门槛 / 搜索范围**不放在这个弹窗里** ——
    //   它们是全局设置，藏在按键弹窗里用户根本找不到（实机 DOM 探针确认过）。
    //   已移到按键页顶部，见 buildClickTune()。

    // 「点完回到原位再点一下」：默认开。点目标按钮会让焦点落到按钮上，
    // 之后 OK→Enter 就发不出去（真机现象）；点回原位能把焦点还给输入框。
    const back = document.createElement('label');
    back.style.cssText = 'display:inline-flex;align-items:center;gap:6px;margin-left:10px;font-size:12.5px;color:#556';
    const backCB = document.createElement('input');
    backCB.type = 'checkbox';
    backCB.checked = !!(window.__clickBack !== false);
    backCB.onchange = async () => { await B.setClickReturnOrigin(backCB.checked ? '1' : '0'); };
    back.appendChild(backCB);
    back.appendChild(document.createTextNode('点完回到原位再点一下（恢复焦点）'));
    mo.appendChild(back);

    // ── 语音键预设（参考同类项目：语音键本质就是一条普通按键映射）──
    //   ⚠ Windows 上**无法注入 Fn** —— Fn 没有标准 HID 键值，只能由键盘固件处理。
    const vp = document.createElement('div');
    vp.style.cssText = 'margin-top:8px;font-size:12.5px;color:#556';
    const cap2 = document.createElement('span');
    cap2.textContent = '语音键预设：';
    vp.appendChild(cap2);
    [['右 Alt（豆包）', '165'], ['Ctrl+Win（微信等）', '17,91'], ['右 Win', '92'], ['F11', '122']]
      .forEach(([label, csv], i) => {
        const b = document.createElement('button');
        b.className = 'btn ghost';
        b.style.marginLeft = i === 0 ? '4px' : '6px';
        b.textContent = label;
        b.onclick = async () => {
          // ★★ 2026-10-27 真机 bug：这里原来**只往后端存**，没同步弹窗的本地状态
          //   （kbMods / kbKey）⇒「当前: …」仍显示旧值；用户再点「保存映射」时
          //   comboVks() 拿的是**旧选择**，又把旧值写回去了 —— 表现为"改完自动变回"。
          //   所以：先把预设写进本地状态并重绘，再落盘。
          const NM = { 0xA5: 'RightAlt', 0xA4: 'LeftAlt', 0xA3: 'RightCtrl', 0xA2: 'LeftCtrl',
                       0xA0: 'LeftShift', 0xA1: 'RightShift', 0x5B: 'Win', 0x5C: 'RightWin' };
          kbMods = new Set();
          kbKey = null; kbKeyLabel = '';
          csv.split(',').map(Number).forEach((vk) => {
            if (vk === 0x11 || vk === 0x12 || vk === 0x10 || vk === 0x5B) kbMods.add(vk);
            else { kbKey = vk; kbKeyLabel = NM[vk] || ('0x' + vk.toString(16).toUpperCase()); }
          });
          paintKb();

          const r = JSON.parse(await B.saveKey('voice', csv));
          toast('语音键已设为 ' + label + '：' + r.message, 6000);
          await refresh();
          paintKb();          // refresh 后窗口还开着时，再刷一次保持显示正确
        };
        vp.appendChild(b);
      });
    const vpn = document.createElement('div');
    vpn.className = 'note';
    vpn.style.marginTop = '6px';
    vpn.innerHTML = '语音键就是一条<b>普通按键映射</b>（在下面按键表里也能直接改）。' +
      'Windows 上<b>无法注入 Fn</b>（Fn 没有标准 HID 键值，只由键盘固件处理），所以不提供 Fn 选项。' +
      '目标输入法必须也把这个键设成"按住说话"。';
    vp.appendChild(vpn);
    mo.appendChild(vp);
  }
  mods.dataset.built = '1';
}

// ── 选中某个按键：遥控器高亮 + 打开下方组合键编辑器 ──
function selectKey(id) {
  selectedKey = id;
  document.querySelectorAll('#keyList .keyrow').forEach((e) => {
    e.classList.toggle('sel', e.dataset.id === id);
  });
  paintRemoteSel();
  const it = ((state && state.keys && state.keys.items) || []).find(x => x.id === id);
  if (!it) return;

  buildKeyboard();
  // 回填当前映射：修饰键 + 主键
  kbMods = new Set(); kbKey = null; kbKeyLabel = '';
  (it.vks || []).forEach((vk) => {
    if (vk === 0x11 || vk === 0x12 || vk === 0x10 || vk === 0x5B) kbMods.add(vk);
    else if (vk === 0xA5) { kbKey = vk; kbKeyLabel = 'RightAlt'; }        // 右 Alt（豆包说话）
    else { kbKey = vk; kbKeyLabel = null; }
  });
  if (kbKey !== null && isKeyAct(kbKey)) {
    kbKeyLabel = ACT_NAME[kbKey] || '动作值';           // 负数：键盘表里根本没有它
  } else if (kbKey !== null && !kbKeyLabel) {
    // 从键盘表里反查显示名
    const all = KB_ROWS.flat().concat(KB_EXTRA);
    const hit = all.find(x => x[1] === kbKey);
    kbKeyLabel = hit ? hit[0] : ('0x' + kbKey.toString(16).toUpperCase());
  }

  $('#comboTitle').textContent = `设置「${it.name}」的快捷键（${it.label}）`;
  $('#comboNote').textContent =
    '点修饰键（Ctrl / Alt / Shift / Win，可多选）再点主键；只选修饰键也可保存；点「不执行」= 该键不注入。'
    + '动作只能单独用：左/右键按住—抬起成对（按住方向 + 按住左键 = 拖拽），'
    + '滚轮按下滚一格，「切换配置组」按下即生效（方块键的出厂值就是它）。';
  const th = $('#comboThumb');
  if (th && it.rect) {
    const { cx, cy, w, h } = it.rect;
    const img = it.img || { w: 179, h: 663 };
    const s = 40 / Math.max(w, h);
    th.style.backgroundSize = `${(img.w * s).toFixed(1)}px ${(img.h * s).toFixed(1)}px`;
    th.style.backgroundPosition = `${(20 - cx * s).toFixed(1)}px ${(20 - cy * s).toFixed(1)}px`;
  }
  // 语音键专用预设：一键套用常见输入法的语音快捷键
  const pre = $('#kbPreset');
  if (pre) {
    if (id === 'voice') {
      pre.style.display = '';
      pre.innerHTML = '<span class="pre-label">输入法语音快捷键预设：</span>'
        // 微信输入法 PC 版「按住说话」= Ctrl+Win（实测其设置里就是这个，不是手机版的长按空格）
        + [['豆包输入法', 0xA5, '按住右 Alt 说话'],
            ['微信输入法', 0x5B, '按住 Ctrl+Win 说话', [0x11]]]
          .map(([n, vk, tip, extra]) =>
            `<button class="pre-btn" data-vk="${vk}" data-extra="${(extra||[]).join(',')}" title="${tip}">${n}</button>`)
          .join('');
      pre.querySelectorAll('.pre-btn').forEach((b) => {
        b.onclick = () => {
          const vk = Number(b.dataset.vk);
          const ex = (b.dataset.extra || '').split(',').filter(Boolean).map(Number);
          kbMods = new Set(ex);
          kbKey = vk;
          kbKeyLabel = vk === 0xA5 ? 'RightAlt' : (vk === 0x5B ? 'Win' : 'Ctrl');
          paintKb();
        };
      });
    } else {
      pre.style.display = 'none';
      pre.innerHTML = '';
    }
  }

  paintKb();
  $('#comboMask').classList.remove('hide');
}

function closeCombo() { $('#comboMask').classList.add('hide'); }

async function editKey(it) {
  const cur = it.vks && it.vks.length ? it.vks.join('+') : '';
  const v = prompt(
    `设置「${it.name}」的快捷键\n\n` +
    '· 单个虚拟键码，或逗号分隔的组合键\n' +
    '· 例：13 = Enter，0x26 = 上，17+65 = Ctrl+A，0xA5 = 右Alt（豆包）\n' +
    '· 留空 = 不注入\n\n当前：' + (cur || '（无）'),
    cur
  );
  if (v === null) return;
  const r = JSON.parse(await B.saveKey(it.id, v.trim()));
  toast(r.message);
  if (r.ok) await refresh();
}

/* ───────── 渲染：环境设置 ───────── */
function kvItem(k, v, cls, sub) {
  return `<div class="item"><div class="k">${k}</div><div class="v ${cls || ''}">${v}`
       + (sub ? `<span class="dim"> · ${sub}</span>` : '') + '</div></div>';
}

function renderEnv(s) {
  const r = s.reg, c = s.cable, b = s.bridge, a = s.admin;

  /* 注册表 */
  setDot('#d-reg', r.ok ? 'on' : 'err');
  $('#regKv').innerHTML =
    kvItem('EtwDropLargeEvents', r.EtwDropLargeEvents, r.EtwDropLargeEvents === 0 ? 'ok' : 'bad') +
    kvItem('EtwLogSensitiveData', r.EtwLogSensitiveData, r.EtwLogSensitiveData === 1 ? 'ok' : 'bad') +
    kvItem('MaxEtwBytes', r.MaxEtwBytes, r.MaxEtwBytes >= 1024 ? 'ok' : 'bad') +
    kvItem('系统启动时间', r.boot) +
    kvItem('注册表修改时间', r.regMtime) +
    kvItem('判定', r.ok ? '已生效' : (r.needReboot ? '改完未重启' : '未配置'), r.ok ? 'ok' : 'bad');
  $('#regNote').className = 'note' + (r.ok ? '' : ' warn');
  $('#regNote').innerHTML = r.ok
    ? '注册表配置已随本次启动加载。<b>MaxEtwBytes</b> 由 <code>bthport.sys</code> 在驱动加载时读取一次，' +
      '若改完没重启，首片 ACL 分片只会记录 8 字节头，每帧只剩 156/320 个样本。'
    : '⚠ 当前配置<b>尚未生效</b>：BthPort 的 ETW 会把首片 ACL 分片截断成 8 字节头，' +
      '导致每帧只有 156/320 个样本（约 48%），音频会音调正常但听感发闷、有杂音。' +
      '<b>请点「应用推荐值」后重启电脑。</b>';

  /* 虚拟声卡 */
  const cOk = c.input && c.output;
  setDot('#d-cable', cOk ? 'on' : 'warn');
  $('#cableKv').innerHTML =
    kvItem('CABLE Input（播放）', c.input ? '已安装' : '未找到', c.input ? 'ok' : 'bad') +
    kvItem('CABLE Output（录音）', c.output ? '已安装' : '未找到', c.output ? 'ok' : 'bad') +
    kvItem('默认麦克风', c.defaultMic ? 'CABLE Output' : '不是 CABLE Output', c.defaultMic ? 'ok' : 'bad') +
    kvItem('驱动版本', c.version || '—');
  $('#cableNote').className = 'note' + (cOk ? '' : ' warn');
  $('#cableNote').innerHTML = cOk
    ? '虚拟声卡已就绪。若输入法仍无声，点「设为默认麦克风」，然后在豆包语音输入法里把麦克风选为 ' +
      '<b>CABLE Output (VB-Audio Virtual Cable)</b>。'
    : '未检测到 VB-CABLE。点「安装 / 修复虚拟声卡」会解压内置的官方驱动包（4.5，含 WHQL 签名，' +
      '可在开启内核隔离的环境下加载）并运行官方安装程序。<b>装完需要重启一次。</b>';

  /* 语音桥 */
  const rt = s.router || {};
  setDot('#d-bridge', !a ? 'err' : (b.running && rt.online ? 'on' : 'warn'));
  // 「最近收到音频」是判断"断在哪儿"最关键的一格：
  //   说话时它应该一直是几百毫秒以内；如果一路涨到几千毫秒，说明**帧根本没来**
  //   （遥控器/BT/ETW 侧的问题），而不是声卡写不进去。
  const silent = (b.silentMs === undefined || b.silentMs === null) ? -1 : b.silentMs;
  // ★ 只有"**正在说话**（会话中）却没帧"才是故障。闲着没按语音键时本来就没有音频，
  //   以前这里一律标红"已断流" —— 界面一空闲就报警，用户会以为坏了（实际被误导过）。
  const inSess = b.inSession === true;
  const silentTxt = silent < 0 ? '尚未收到'
    : silent < 1200 ? `${silent} ms 前 ✓`
    : inSess ? `${(silent / 1000).toFixed(1)} s 前 ← 说话中却断了！`
    : `${(silent / 1000).toFixed(1)} s 前（没在说话）`;
  const silentCls = silent < 0 ? '' : (silent < 1200 ? 'ok' : (inSess ? 'bad' : ''));
  // ★ 2026-10-27：**分级显示**。正常使用时只需要三格：运行状态 / 最近收到音频 / 真丢帧。
  //   其余（帧数、样本、空档、丢帧分类、写入包数、抖动缓冲、权限）属于"出问题才看"，
  //   折进下面的「诊断详情」，避免平时面板被 10 行数字占满。
  $('#bridgeKv').innerHTML =
    kvItem('运行状态', b.zombie ? '僵死（进程在但已停止上报）' : (b.running ? '运行中' : '已停止'),
           b.zombie ? 'bad' : (b.running ? 'ok' : '')) +
    kvItem('最近收到音频', silentTxt, silentCls) +
    // ★ 2026-10-19：**跳号 ≠ 丢帧**。遥控器不说话时根本不发帧，序号自然跳过
    //   （实测 642 帧跳号全部对应静音期，真丢帧 = 0，却被红字报成"丢失"）。
    //   现在只把"真丢帧"标红；静音跳号用灰字说明。
    kvItem('丢帧（真丢）', (b.seqLost || 0), (b.seqLost || 0) > 0 ? 'bad' : 'ok',
           (b.seqJumps || 0) > 0
             ? `序号跳 ${b.seqJumps} 次：静音期未发送 ${b.seqSilence || 0} 帧（正常）`
             : '');

  // ── 以下属于"出问题才看"的诊断明细 ──
  $('#bridgeDiagKv').innerHTML =
    kvItem('虚拟声卡通道', rt.online ? '已就绪' : (rt.error || '未启动'), rt.online ? 'ok' : 'bad') +
    kvItem('音频帧', b.frames || 0) +
    kvItem('16k 样本', (b.samples || 0) + (b.seconds ? `（${b.seconds} s）` : '')) +
    kvItem('会话 / 空档', `${b.sessions || 0} / ${b.gaps || 0}` +
                        ((b.maxGapMs || 0) > 0 ? `（最长 ${b.maxGapMs} ms）` : '')) +
    kvItem('丢帧分类', '组装失败 ' + (b.lostCont || 0)
                        + ' · 长度不认识 ' + (b.oddFrag || 0)
                        + ' · 分片被截断 ' + ((b.truncCont || 0) + (b.truncatedFirst || 0))
                        + ' · 孤续片 ' + (b.orphanCont || 0),
           (b.lostCont || 0) + (b.oddFrag || 0) + (b.truncCont || 0)
             + (b.truncatedFirst || 0) + (b.orphanCont || 0) + (b.shortPdu || 0) > 0
             ? 'bad' : 'ok',
           (b.shortPdu || 0) > 0 ? `怪长帧 ${b.shortPdu}` : '') +
    kvItem('写入 CABLE', (rt.packets || 0) + ' 包') +
    kvItem('抖动缓冲', (rt.jitter
              ? `积压 ${rt.jitter.backlogMs || 0} ms（峰值 ${rt.jitter.peakBacklogMs || 0}）`
              : '未启用'),
           !rt.jitter ? '' :
             ((rt.jitter.peakBacklogMs || 0) > (rt.jitter.capMs || 64) * 0.95 ? 'warn' : 'ok'),
           rt.jitter
             ? `起播 ${rt.jitter.effPreRollMs || rt.jitter.preRollMs} / 上限 ${rt.jitter.capMs} ms`
               // 自适应调过就标出来：起播大于配置值 = 最近出过断音、自动加了缓冲
               + ((rt.jitter.effPreRollMs || 0) > (rt.jitter.preRollMs || 0)
                   ? `（自适应 +${rt.jitter.effPreRollMs - rt.jitter.preRollMs}）` : '')
               + ` · 补静音 ${rt.jitter.underruns || 0} 次`
               + ` · 丢最旧 ${rt.jitter.droppedBytes || 0} 字节`
             : '') +
    kvItem('管理员权限', a ? '已具备' : '缺失', a ? 'ok' : 'bad') +
    // 卡键排查：系统当前认为按着的键（空 = 正常）
    (s.heldNow
      ? kvItem('⚠ 系统认为按住', s.heldNow, 'bad', '若这里出现键名而你没按它 → 就是卡键')
      : '');
  // 「按键链路静默失效」探针：只在桥"收到报告却解析不出按键"时才有内容。
  // 判据在 KeyPathWatch 里（空闲不报 —— 这条今天已经踩过两次坑）。
  $('#keyPathNote').textContent = b.keyPathHint || '';
  $('#bridgeNote').className = 'note' + (a ? '' : ' warn');
  $('#bridgeNote').innerHTML = a
    ? (b.zombie
        ? '<b>语音桥僵死</b>：进程还在但已停止上报统计 —— 这种状态靠"进程是否存活"看不出来，' +
          '会自动重启；若反复出现，看 <code>bridge.log</code>。'
        : '语音桥从 BthPort ETW 取完整音频帧（每帧 320 样本 @16 kHz），可选高通去低频漂移，' +
          '再以 48 kHz 写入 <b>CABLE Input</b>。<b>按住遥控器语音键说话即可。</b>') +
      (rt.device ? `<br>输出设备：${rt.device}` : '') +
      `<br><span class="dim">排查用：「最近收到音频」说话时应始终 &lt;1 秒；` +
      `一直涨 = 帧没来（遥控器/BT/ETW）。<br>` +
      `「丢失帧数」用帧头序号量出来，是<b>真丢了多少帧</b>；「丢帧分类」说明丢在哪一层 —— ` +
      `组装失败=续片没到（事件被丢或线路丢）；长度不认识=分片长度/标志变了（★ 不是丢，是变了，` +
      `说明帧格式与预期不同）；分片被截断=ETW 截断负载（改注册表 MaxEtwBytes 后<b>必须重启</b>）。<br>` +
      `逐秒明细见 <code>bridge.log</code> 的「音频丢失明细」。</span>`
    : '语音桥需要<b>管理员权限</b>才能创建 ETW 会话（<code>StartTrace</code> 要求 Administrators）。' +
      '请点下面的「以管理员身份重启本程序」。';

  $('#btnBridge').textContent = b.running ? '停止语音桥' : '启动语音桥';
  $('#btnBridge').disabled = !a && !b.running;

  const sw = s.swallow || {};
  setDot('#d-perm2', sw.running ? 'on' : 'err');
  $('#btnInject').textContent = s.inject ? '关闭快捷键注入' : '开启快捷键注入';
  const voiceBind = (s.keys.items.find((k) => k.id === 'voice') || {}).binding || '未设置';
  // ★ 按钮/说明都跟着**当前映射**走：以前写死"右 Alt"，用户一改映射就变成错的说明
  const btnTI = $('#btnTestInject');
  if (btnTI) btnTI.textContent = voiceBind === '未设置' ? '测试注入语音键' : `测试注入 ${voiceBind}`;
  // 只有 ETW 一种来源了：不再有"键盘模式/拦截/漏键"这一套说明
  // ★★ 2026-10-27：原来这段是**写死的绿色文案**，不管设备实际是什么状态都说
  //   "已被当作非键盘、不会输出任何字符" —— 用户因此误以为没问题，实际 m 一直在漏。
  //   现在按后端的三态如实显示：Absent（未连接）/ Keyboard（仍是键盘）/ NotKeyboard（已切换）。
  const rk = s.remoteKb || 'Absent';
  setDot('#d-perm2', rk === 'NotKeyboard' ? 'on' : (rk === 'Keyboard' ? 'bad' : 'warn'));
  $('#injectNote').className = 'note' + (rk === 'Keyboard' ? ' warn' : '');
  if (rk === 'Keyboard') {
    $('#injectNote').innerHTML = (state && state.driverFree)
      ? `无驱动模式：遥控器保持键盘，它自带的 <b>m</b> 由钩子按「连发特征」吞掉` +
        `（已吞 <b>${(state && state.ghostSwallowed) || 0}</b> 次；开头第 1 个可能漏，属正常）。`
      : `⚠ 遥控器仍被系统当作 <b>键盘</b> → 按键会直接打出字符（按住语音键就是一串 <b>m</b>）。<br>` +
        `请在下面点「<b>切换设备驱动</b>」（需要管理员；先把遥控器按醒）。` +
        `按键仍从 <b>BthPort ETW</b> 读取，但系统这一层拦不住。`;
  } else if (rk === 'Absent') {
    $('#injectNote').innerHTML =
      `遥控器<b>当前未连接</b>（休眠 / 未配对）→ <b>无法判断</b>驱动是否已切换。<br>` +
      `按一下遥控器任意键唤醒，等 2 秒后这里的判断才作数。`;
  } else {
    $('#injectNote').innerHTML =
      `遥控器已被系统当作<b>普通 HID 设备</b>（非键盘）→ <b>不会输出任何字符</b>，` +
      `无需拦截，也没有「丢 UP 卡键」风险。按键从 <b>BthPort ETW</b> 读取。`;
  }
  $('#injectNote').innerHTML +=
    `<br>卡键自动解锁：设备移除<b>即时响应</b> · ` +
    ((s.stuckGuard || {}).voiceAudioRelease
      ? `语音键判据 音频静默 ${((s.stuckGuard || {}).voiceSilentMs || 60000) / 1000}s（兜底已开启） · `
      : `语音键<b>只按 UP 释放</b>（音频兜底已关闭） · `) +
    `已解锁 <b>${((s.stuckGuard || {}).unlockCount) || 0}</b> 次` +
    (((s.stuckGuard || {}).lastUnlock) ? `（${(s.stuckGuard || {}).lastUnlock}）` : '');
  // ── 按键来源 ──
  // 日志开关的显示状态（绑定在 bindUI 里，因为这里才拿得到 s）
  const logT = $('#logToggle');
  if (logT) logT.checked = s.logEnabled === true;
  const logSw = $('#logSwitch');
  if (logSw) logSw.classList.toggle('on', s.logEnabled === true);
  // 语音输入法（首页「第一次使用」里的分段按钮，2026-10-29 从环境设置搬过来）
  const imeCur = s.imeProfile || 'doubao';
  document.querySelectorAll('.ime-seg button[data-ime]').forEach(btn => {
    btn.classList.toggle('on', btn.dataset.ime === imeCur);
  });
  const imeHint = $('#imeHint');
  if (imeHint) {
    const det = s.imeDetected || {};
    const hit = [det.doubao && '豆包', det.wechat && '微信输入法'].filter(Boolean);
    imeHint.textContent =
      (hit.length ? `检测到：${hit.join(' / ')}。` : '未检测到豆包 / 微信输入法。')
      + (s.imeVoice ? ` 语音键 = ${s.imeVoice}。` : '')
      + ' 微信输入法需把「麦克风」选成 CABLE Output。';
  }

  // 零漏模式
  const zgT = $('#zeroGhostToggle');
  if (zgT) zgT.checked = s.zeroGhost === true;
  const zgSw = $('#zeroGhostSwitch');
  if (zgSw) zgSw.classList.toggle('on', s.zeroGhost === true);

  // 向导条：未完成时显示当前步骤；当前步没通过就禁用「下一步」
  (() => {
    const bar = $('#wizardBar');
    if (!bar) return;
    const wz = s.wizard;
    const done = !wz || wz.done;
    bar.style.display = done ? 'none' : '';
    if (!done) {
      const total = wz.total || 4;
      const idx = Math.min(wz.passed || 0, total - 1);
      const cur = (wz.items || [])[idx] || {};
      const st = $('#wizardStep'); if (st) st.textContent = `第 ${idx + 1} / ${total} 步`;
      const hint = $('#wizardHint'); if (hint) hint.textContent = cur.hint || '';
      const btn = $('#wizardNext');
      if (btn) { btn.disabled = !wz.canAdvance; btn.textContent = wz.canAdvance ? '下一步' : '先完成这一步'; }
      $$('#journeyList .jstep').forEach((el, i) => el.classList.toggle('pending', i !== idx));
    } else {
      $$('#journeyList .jstep').forEach((el) => el.classList.remove('pending'));
    }
  })();

  // 无驱动模式开关
  const dfT = $('#driverFreeToggle');
  if (dfT) dfT.checked = s.driverFree === true;
  const dfSw = $('#driverFreeSwitch');
  if (dfSw) dfSw.classList.toggle('on', s.driverFree === true);

  // 事件日志开关（第二级）的初始态
  // 截图点击调参的当前值（不覆盖正在编辑的输入框）
  const minEl = $('#clickMinScore');
  if (minEl && document.activeElement !== minEl) minEl.value = String((state && state.clickMinScore) ?? 0.75);
  const scopeEl = $('#clickScope');
  if (scopeEl && document.activeElement !== scopeEl) scopeEl.value = String((state && state.clickSearchScope) ?? 1);

  const noteT = $('#noteToggle');
  if (noteT) noteT.checked = s.keepEventLog !== false;
  const noteSw = $('#noteSwitch');
  if (noteSw) noteSw.classList.toggle('on', s.keepEventLog !== false);

  const logN = $('#logNote');
  if (logN) logN.textContent = s.logEnabled
    ? '日志已开启：会写 key-events.log / bridge.log / router.log 等，占用少量磁盘与 I/O。排查完建议关闭。'
    : '';

  const ks = s.keysource || {};
  const ke = ks.etw || {};
  const isEtw = ks.mode === 'etw';
  // 发布版默认不写日志：整个"日志"相关的 UI（这一格 + 清空按钮）都按开关走
  const logOn = s.logEnabled === true;
  const clrBtn = $('#btnClearLog');
  if (clrBtn) clrBtn.hidden = !logOn;
  const srcName = 'BthPort ETW';   // 只有这一种来源（键盘模式/钩子已移除）
  $('#keySrcKv').innerHTML =
    kvItem('按键来源', srcName, ks.mode === 'hook' ? '' : 'ok') +
    kvItem('ETW 采集器', ke.online
             ? `已监听 127.0.0.1:${ke.port} · 按下 ${ke.keyDowns || 0} / 松开 ${ke.keyUps || 0} 次`
             : '未启动',
           ke.online ? 'ok' : 'bad') +
    // ⚠ 一次正常按键 = 1 按下 + 1 松开 = **2 个事件**。把三者分开列，
    //   免得"48 次"被读成"按了 48 下"（用户 2026-10-18 问过一次）。
    kvItem('事件 vs 按键', (ke.keyDowns || 0) === (ke.keyUps || 0)
             ? `共 ${ke.events || 0} 个事件 = ${ke.keyDowns || 0} 次按键 × 2（按下+松开）`
             : `共 ${ke.events || 0} 个事件 · 按下 ${ke.keyDowns || 0} ≠ 松开 ${ke.keyUps || 0}（有丢 UP）`,
           (ke.keyDowns || 0) === (ke.keyUps || 0) ? '' : 'warn') +
    kvItem('ETW 投递延迟', ke.latencySamples > 0
             ? `平均 ${ke.avgLatencyMs} ms · 最大 ${ke.maxLatencyMs} ms（${ke.latencySamples} 次采样）`
             : '等待采样',
           (ke.maxLatencyMs || 0) > 200 ? 'bad' : 'ok') +
    kvItem('最近按键', ke.lastButton ? `${ke.lastButton}（usage ${ke.lastUsage}${ke.pressed ? ' 按住' : ''}）` : '—') +
    // ★ 2026-10-27：「随机冒出字母」的关键判据 —— 遥控器是否发过**不认识的 usage**。
    //   0x04~0x1D 在 HID 表里正好是字母 a~z：若固件偶尔发这种码，就会被当成字母。
    //   （我们的注入只注入"认识且已映射"的键，所以未知码本身不会打出字；
    //     但这个计数 > 0 说明固件在发怪报文，是排查的起点。）
    (ke.unknownUsage > 0
      ? kvItem('⚠ 未知报文', `${ke.unknownUsage} 次`, 'warn',
               '遥控器发过不认识的 usage（0x04~0x1D 对应字母 a~z）—— 这就是"随机字母"的来源')
      : kvItem('未知报文', '0 次', 'ok')) +
    // 日志只在打开诊断开关时才有意义；默认关就不显示这一格（否则用户会以为"还有日志"）
    (logOn && ks.logPath ? kvItem('按键日志', '已写入 ' + ks.logPath) : '');
  $('#keySrcNote').className = 'note' + (ks.stillKeyboard ? ' warn' : '');
  // 注意：driverFree / ghostSwallowed 在 **GetState 顶层**（不在 keySrc 子对象里）
  $('#keySrcNote').innerHTML = s.driverFree
    // 无驱动模式：遥控器**就是键盘**，这里不能再喊"请安装虚拟键盘驱动"（那恰好会推翻本模式）
    ? '<b>无驱动模式</b>：遥控器保持键盘，它自带的 m 由钩子按「连发特征」拦掉' +
      '（已拦 <b>' + (s.ghostBursts || 0) + '</b> 段 / 共 ' + (s.ghostSwallowed || 0) + ' 下' +
      '；最近一次：' + (s.ghostLastAgo < 0 ? '从未' : (s.ghostLastAgo <= 3 ? `${s.ghostLastAgo} 秒前` : `${s.ghostLastAgo} 秒前（已停）`)) +
      '。每段开头可能漏 1 个 m，属正常）。'
    : (ks.stillKeyboard
      ? '⚠⚠ <b>驱动还没换！</b>系统仍把遥控器当键盘 → <b>按遥控器会同时打出 m</b>。' +
        '请点下面的「<b>安装虚拟键盘驱动</b>」，把它的驱动从「HID Keyboard Device」换成' +
        '「HID-compliant device」。' + (ks.guard ? `<br>${ks.guard}` : '')
      : '设备已不是键盘 ✓ 按键从 <b>BthPort ETW</b> 读取，不装键盘钩子、不吞键 —— <b>没有卡键风险</b>。' +
        (ke.error ? `<br>采集器错误：${ke.error}` : ''));

  // ── 环境自检 ──
  const st = s.setup || {};
  const items = [['cable','虚拟声卡 VB-CABLE'],['mic','默认录音设备 = CABLE Output'],
                 ['winuhid','虚拟键盘驱动 WinUHid'],['device','遥控器已不是键盘'],
                 ['bridge','语音桥运行中']];
  const done = items.filter(([k]) => st[k] && st[k].ok).length;
  setDot('#d-setup', st.ready ? 'on' : 'warn');
  $('#setupKv').innerHTML = items.map(([k, label]) => {
    const it = st[k] || {};
    return kvItem((it.ok ? '✔ ' : '✘ ') + label, it.ok ? '就绪' : (it.hint || '未就绪'), it.ok ? 'ok' : 'bad');
  }).join('');
  $('#setupNote').className = 'note' + (st.ready ? '' : ' warn');
  $('#setupNote').innerHTML = st.ready
    ? `全部就绪（${done}/${items.length}）。语音桥${s.autoStart ? '会在启动后自动拉起' : '需要手动启动'}。`
    : `还有 ${items.length - done} 项没就绪。点「一键完成全部」自动处理；需要 UAC 的步骤会弹窗。`;
  $('#btnAutoStart').textContent = `启动后自动拉起语音桥：${s.autoStart ? '已开启' : '已关闭'}`;
  $('#btnAutoRun').textContent = `开机自启：${s.autoRun ? '已开启' : '已关闭'}`;
  $('#btnStartMin').textContent = `启动时进托盘：${s.startMinimized ? '已开启' : '已关闭'}`;
  $('#btnAutoDrv').textContent = `启动自动切驱动：${s.autoSwitchDriver ? '已开启' : '已关闭'}`;
  $('#btnAutoMic').textContent = `默认麦克风被改走时自动改回：${s.autoKeepMic ? '已开启' : '已关闭'}`;
  // 把"当前真正的默认录音设备"直接显示出来 —— Windows 会在蓝牙耳机接入/断开时
  // 把它悄悄改走，那样输入法读到的是别的设备（表现为「录音启动失败」或通话无波形）
  const dm = s.defaultMicName || '?';
  const dmOk = dm.toLowerCase().includes('cable output');
  $('#defaultMicNote').className = 'note' + (dmOk ? '' : ' warn');
  $('#defaultMicNote').innerHTML = dmOk
    ? `当前默认录音设备：<b>${dm}</b> ✓`
    : `当前默认录音设备：<b>${dm}</b> —— <b>不是 CABLE Output</b>，输入法/通话软件会读错设备。` +
      (s.autoKeepMic
        ? '已开启「自动改回」（一分钟内会改回来）。'
        : '请<b>在输入法设置里把麦克风选成 CABLE Output</b>；' +
          '要改系统默认，点下方「设为 CABLE Output」。' +
          '<br>（程序默认<b>不自动抢</b>默认设备 —— 自动改会和输入法的设备缓存打架，' +
          '反而出现"录音启动失败/无波形"。）');

  const uh = s.winuhid || {};
  setDot('#d-uhid', uh.ready ? 'on' : 'warn');
  $('#uhidKv').innerHTML =
    kvItem('虚拟键盘驱动', uh.ready ? `已就绪（接口版本 ${uh.driverVersion}）` : '未安装 / 未就绪',
           uh.ready ? 'ok' : 'bad') +
    kvItem('WinUHid.dll', uh.dllFound ? '已内置' : '缺失', uh.dllFound ? 'ok' : 'bad') +
    kvItem('注入通道', uh.ready ? '虚拟 HID（输入法不过滤）' : '回退 SendInput（输入法会过滤）',
           uh.ready ? 'ok' : 'bad');
  $('#uhidNote').className = 'note' + (uh.ready ? '' : ' warn');
  $('#uhidNote').innerHTML = uh.ready
    ? '虚拟键盘已就绪：注入的按键在系统里与真实键盘完全等价，输入法不会过滤。'
    : '⚠ <b>豆包等输入法会主动过滤 SendInput 注入的按键</b>（检测 keyboard hook 的 INJECTED 标志），' +
      `这就是"${voiceBind} 注入了但豆包没反应"的原因。点「安装虚拟键盘驱动」装上 WinUHid（UMDF 用户态驱动，` +
      '不受内核隔离限制），装完回来点「测试注入」确认。' +
      (uh.error ? `<br>当前状态：${uh.error}` : '');

  // 面板精简：去掉「拦截状态」（ETW 模式下无意义）与「豆包设置对应」（与上行重复）；
  // 「最近按键」只保留 keySrcKv 里的那格（带 usage / 按住态），这里不再重复展示
  $('#injectKv').innerHTML =
    kvItem('语音键原始按键', `<b>M</b>（HID 0x10）`) +
    kvItem('按住时注入', `<b>${voiceBind}</b>（豆包「长按模式」用的就是它）`);

}

/* ───────── 刷新 ───────── */
async function refresh() {
  if (!B) return;
  try {
    state = JSON.parse(await B.getState());
  } catch (e) { return; }
  renderJourney(state);
  renderOverview(state);
  renderKeys(state);
  renderEnv(state);
  renderMouse(state);
}

/* ── 窗口藏进托盘后停掉 2 秒轮询 ──
   本程序是常驻托盘应用，窗口大部分时间是隐藏的。原来 refresh 每 2 秒照跑：
   一次跨进程 getState()（不小的 JSON）+ 三次全量 DOM 重绘，而根本没人看。
   由 C# 侧 OnVisibleChanged 调 window.setUiVisible() 控制；
   重新露出时立刻补刷一次，避免显示旧数据。 */
window.setUiVisible = function (v) {
  if (timer) { clearInterval(timer); timer = null; }
  if (v) {
    refresh();
    timer = setInterval(refresh, 2000);
    if (!$('#page-mouse') || !$('#page-mouse').classList.contains('hidden')) startMousePad();
  } else {
    stopMousePad();     // 窗口藏起来了就别再轮询校准区
  }
};

/* ───────── 鼠标模式页 ─────────
   三块：模式开关 / 手感（滑条）+ 开关 / 实时校准区。
   校准区是这一页的重点 —— 按住遥控器方向键就能看到光点跟着走、右下角给实时速度，
   所以调滑条时**手感即时可感**，不用"改数字 → 保存 → 切出去试"。 */
const MOUSE_DEFAULTS = {
  tickHz: 60, minPxPerSec: 60, maxPxPerSec: 900, rampMs: 700, rampExponent: 2,
  normalizeDiagonal: true, directionHoldTimeoutSec: 8, buttonHoldTimeoutSec: 10,
  rememberMode: false, calibrate: true,
  wheelOnVolumeKeys: false, wheelMinPerSec: 3, wheelMaxPerSec: 16,
};
let mousePadTimer = null;
let mouseCfgTimer = null;
let mouseDotPos = { x: 50, y: 50 };

/* .note 只在出问题时显示 —— 所以清空时要摘掉 warn 类 */
function setNote(id, msg) {
  const n = $(id);
  if (!n) return;
  n.textContent = msg || '';
  n.classList.toggle('warn', !!msg);
}

function paintSlider(el) {
  const b = el.parentElement.querySelector('.val');
  if (b) b.textContent = Number(el.value).toFixed(+(el.dataset.dec || 0)) + (el.dataset.unit || '');
}

/* 把页面上所有滑条/开关/单选收成一个对象发给宿主（只有鼠标页的，用 #page-mouse 限定作用域） */
function collectMouseCfg() {
  const o = {};
  for (const el of $$('#page-mouse input[type=range]')) o[el.dataset.k] = Number(el.value);
  for (const el of $$('#page-mouse input[type=checkbox]')) o[el.dataset.k] = el.checked;
  return o;
}

/* 拖动时只更新数字；松手（change）或 250ms 没动才真正下发 —— 免得拖一次发几十个包 */
function pushMouseCfg(now) {
  clearTimeout(mouseCfgTimer);
  const send = async () => {
    if (!B) return;
    try {
      setNote('#mouseCfgNote', '');
      const msg = await B.setMouseConfig(JSON.stringify(collectMouseCfg()));
      toast(msg, 3000);
    } catch (e) {
      setNote('#mouseCfgNote', '保存失败：' + (e && e.message ? e.message : e));
    }
  };
  if (now) send(); else mouseCfgTimer = setTimeout(send, 250);
}

/// 鼠标组在配置组列表里的显示名（找不到就退回固定名）—— 只用于按钮文案
function KeymapsMouseName(groups) {
  const g = (groups || []).find(x => x.kind === 'mouse');
  return g ? g.name : '鼠标模式';
}

function renderMouse(s) {
  const m = (s && s.mouse) || {};
  const on = m.mode === 'mouse';
  setDot('#d-mouse', on ? 'on' : '');
  const groups = ((s && s.groups) || {}).items || [];
  const curG = groups[((s && s.groups) || {}).active || 0] || {};
  $('#mouseKv').innerHTML =
    kvItem('当前模式', on ? '<b>鼠标模式</b>' : '键盘模式',
           on ? 'ok' : '') +
    kvItem('由哪个配置组决定', `「${curG.name || '—'}」`
           + (curG.kind === 'mouse' ? '（鼠标组）' : ''),
           curG.kind === 'mouse' ? 'ok' : '') +
    kvItem('方向键', on ? '移动 · 按住越久越快' : '走当前配置组的映射') +
    kvItem('确认 / info', '走当前配置组的映射（可改成鼠标左/右键）') +
    kvItem('斜向', on ? '支持同时按住 上+左，且已归一化' : '—');
  $('#btnMouseToggle').textContent = on ? '切回上一个配置组' : `切到「${KeymapsMouseName(groups)}」`;


  /* 回写控件状态：正在被用户拖动的那一个跳过，免得把他的手指"拽回去" */
  for (const el of $$('#page-mouse input[type=range]')) {
    const k = el.dataset.k;
    if (m[k] === undefined || el.dataset.dirty === '1') continue;
    el.value = m[k];
    paintSlider(el);
  }
  for (const el of $$('#page-mouse input[type=checkbox]')) {
    const k = el.dataset.k;
    if (m[k] === undefined || el.dataset.dirty === '1') continue;
    el.checked = !!m[k];
    const sw = el.closest('.switch');
    if (sw) sw.classList.toggle('on', el.checked);
  }

  $('#mouseLiveKv').innerHTML =
    kvItem('实测增益', (m.gain === undefined ? 1 : m.gain) + '×'
      + (m.accelSuspected ? ' ★ 系统「指针精确度」可能开着，速度会有偏差' : '')) +
    kvItem('本次运行', (m.frames || 0) + ' 帧 · 累计 ' + (m.pixelsMoved || 0) + ' px') +
    kvItem('安全网触发', '方向超时 ' + (m.dirTimeouts || 0)
      + ' · 键超时 ' + (m.btnTimeouts || 0) + ' · 孤儿回收 ' + (m.orphanReaps || 0)) +
    kvItem('最近一次回收', m.lastReap || '—');

  if (!on) stopMousePad();
}

function startMousePad() {
  if (mousePadTimer || !B) return;
  const page = $('#page-mouse');
  if (!page || page.classList.contains('hidden')) return;
  mouseDotPos = { x: 50, y: 50 };
  const apply = (dot) => { dot.style.left = mouseDotPos.x + '%'; dot.style.top = mouseDotPos.y + '%'; };
  apply($('#mouseDot'));
  mousePadTimer = setInterval(async () => {
    if (!B) return;
    let d;
    try { d = JSON.parse(await B.mouseSample()); } catch (e) { return; }
    const dot = $('#mouseDot');
    if (d.mode !== 'mouse') {
      dot.classList.remove('on');
      $('#mousePadHint').textContent = '先切到鼠标模式，再按住遥控器方向键试试';
      $('#mouseSpeedLive').textContent = '';
      return;
    }
    dot.classList.add('on');
    $('#mousePadHint').textContent = '';
    /* 光点 = **光标真实位置**映射到校准区（一台小地图），不是从固定点累加位移 ——
       累加会漂、而且和真实光标对不上。留 4%/8% 边距免得贴边被裁。 */
    const vw = Math.max(d.vw - 1, 1), vh = Math.max(d.vh - 1, 1);
    const px = Math.min(1, Math.max(0, (d.x - d.vx) / vw));
    const py = Math.min(1, Math.max(0, (d.y - d.vy) / vh));
    dot.style.left = (4 + px * 92) + '%';
    dot.style.top = (8 + py * 84) + '%';
    if (d.pxPerSec) $('#mouseSpeedLive').textContent = Math.round(d.pxPerSec) + ' px/秒';
  }, 50);
}

function stopMousePad() {
  if (mousePadTimer) { clearInterval(mousePadTimer); mousePadTimer = null; }
  const dot = $('#mouseDot');
  if (dot) dot.classList.remove('on');
  const sp = $('#mouseSpeedLive');
  if (sp) sp.textContent = '';
}

/* ───────── 绑定 ───────── */
function bindUI() {
  // ③ 四步清单：点一步跳到对应页面；用户手动展开后尊重其选择
  const jr = $('#journey');
  if (jr) {
    jr.addEventListener('toggle', () => { if (jr.open) jr.dataset.autoClosed = '1'; });
    $$('#journeyList .jstep').forEach((b) => { b.onclick = () => goto(b.dataset.go); });
  }
  // 截图点击的全局调参（按键页顶部）——建一次即可，内部有 dataset.built 幂等守卫
  buildClickTune();

  $$('.nav-btn').forEach((b) => (b.onclick = () => goto(b.dataset.page)));

  /* ── 鼠标模式页 ── */
  for (const el of $$('#page-mouse input[type=range]')) {
    el.addEventListener('input', () => { el.dataset.dirty = '1'; paintSlider(el); });
    el.addEventListener('change', () => { el.dataset.dirty = '0'; pushMouseCfg(true); });
  }
  for (const el of $$('#page-mouse input[type=checkbox]')) {
    el.addEventListener('change', () => {
      const sw = el.closest('.switch');
      if (sw) sw.classList.toggle('on', el.checked);
      pushMouseCfg(true);
    });
  }
  $('#btnMouseToggle').onclick = async () => {
    try { toast(await B.setMouseMode('toggle'), 3400); }
    catch (e) { toast('切换失败：' + (e && e.message ? e.message : e), 4000); }
    await refresh();
  };
  $('#btnMouseCalib').onclick = async () => {
    try { toast(await B.mouseCalibrate(), 4200); }
    catch (e) { toast('自校准失败：' + (e && e.message ? e.message : e), 4000); }
    await refresh();
  };
  $('#btnMouseReset').onclick = async () => {
    for (const el of $$('#page-mouse input[type=range]')) el.dataset.dirty = '0';
    for (const el of $$('#page-mouse input[type=checkbox]')) el.dataset.dirty = '0';
    try { toast(await B.setMouseConfig(JSON.stringify(MOUSE_DEFAULTS)), 3600); }
    catch (e) { toast('恢复失败：' + (e && e.message ? e.message : e), 4000); }
    await refresh();
  };

  $('#btnReset').onclick = async () => {
    if (!confirm('恢复所有按键的默认映射？')) return;
    const r = JSON.parse(await B.resetKeys());
    toast(r.message); await refresh();
  };

  $('#btnApplyReg').onclick = async () => {
    const r = JSON.parse(await B.applyRegistry());
    toast(r.message, 4200); await refresh();
  };
  $('#btnRecheck').onclick = async () => {
    try {
      state = JSON.parse(await B.refreshAll());
      // ★ 各页独立 try/catch：任何一页渲染出错都不再拖垮其它页
      //   （曾经因为 renderOverview 抛异常，导致按键映射页整页空白）
      try { renderOverview(state); } catch (e) { console.error('renderOverview', e); }
      try { renderKeys(state); } catch (e) { console.error('renderKeys', e); }
      try { renderEnv(state); } catch (e) { console.error('renderEnv', e); }
    }
    catch (e) { await refresh(); }
    toast('已重新检测');
  };
  $('#btnReboot').onclick = async () => {
    if (!confirm('立即重启这台电脑？\n\n注册表配置需要重启才会被蓝牙驱动加载。')) return;
    await B.reboot();
  };

  $('#btnInstallCable').onclick = async () => {
    if (!confirm('将解压内置的 VB-CABLE 官方驱动包并启动安装程序。\n安装完成后需要重启。继续？')) return;
    toast('正在准备官方安装程序…', 8000);
    const r = JSON.parse(await B.installCable());
    toast(r.message, 6000); await refresh();
  };
  $('#btnSetMic').onclick = async () => {
    const r = JSON.parse(await B.setDefaultMic());
    toast(r.message, 4200); await refresh();
  };
  $('#btnOpenCable').onclick = () => B.openUrl('https://vb-audio.com/Cable/');

  $('#btnBridge').onclick = async () => {
    const running = state && state.bridge.running;
    const r = JSON.parse(await (running ? B.stopBridge() : B.startBridge()));
    toast(r.message, 4200); await refresh();
  };
  $('#btnInject').onclick = async () => {
    const want = !(state && state.inject);
    const r = JSON.parse(await B.setInject(want));
    toast(r.message); await refresh();
  };
  // 日志开关：**绑定**放这里（`bindUI` 没有 `s`，渲染部分在 renderKeys 里）。
  // ⚠ 预览模式（浏览器直接打开）没有宿主桥，`B.setLog` 不存在 —— 那时不绑事件，只显示状态。
  const logTB = $('#logToggle');
  const hasBridge = !!(window.chrome && window.chrome.webview && window.chrome.webview.postMessage);
  if (logTB && hasBridge) {
    logTB.onchange = async () => {
      const r = JSON.parse(await B.setLog(logTB.checked ? '1' : '0'));
      logTB.checked = r.logEnabled === true;
      const sw = $('#logSwitch');
      if (sw) sw.classList.toggle('on', logTB.checked);
      toast(logTB.checked ? '已打开诊断日志（会写 key-events.log / bridge.log，排查完请关掉）'
                          : '已关闭诊断日志', 4000);
      refresh();
    };
  }

  // 事件日志开关（第二级）：关掉后连 errors.log 都不写
  const noteTB = $('#noteToggle');
  if (noteTB && hasBridge) {
    noteTB.onchange = async () => {
      const r = JSON.parse(await B.setKeepEventLog(noteTB.checked ? '1' : '0'));
      noteTB.checked = r.keepEventLog === true;
      const sw = $('#noteSwitch');
      if (sw) sw.classList.toggle('on', noteTB.checked);
      toast(r.message, 5000);
      refresh();
    };
  }

  // 向导：下一步 / 重跑
  const wzBtn = $('#wizardNext');
  if (wzBtn && hasBridge) {
    wzBtn.onclick = async () => {
      const r = JSON.parse(await B.wizardNext());
      toast(r.message, 7000);
      await refresh();
    };
  }
  // 导出配置：卸载会删掉配置目录，先让用户能备份
  const expBtn = $('#btnExportConfig');
  if (expBtn && hasBridge) {
    expBtn.onclick = async () => {
      const r = JSON.parse(await B.exportConfig());
      toast(r.ok ? r.message : (r.message || '导出失败'), 9000);
    };
  }
  const wzReset = $('#btnWizardReset');
  if (wzReset && hasBridge) {
    wzReset.onclick = async () => {
      if (!confirm('重新走一遍首次设置向导？')) return;
      const r = JSON.parse(await B.wizardReset());
      toast(r.message, 5000); await refresh();
    };
  }

  // 零漏模式：先吞后补
  const zgT2 = $('#zeroGhostToggle');
  if (zgT2 && hasBridge) {
    zgT2.onchange = async () => {
      const r = JSON.parse(await B.setZeroGhost(zgT2.checked ? '1' : '0'));
      zgT2.checked = r.zeroGhost === true;
      const sw = $('#zeroGhostSwitch');
      if (sw) sw.classList.toggle('on', zgT2.checked);
      toast(r.message, 9000);
      await refresh();
    };
  }

  // 无驱动模式：切驱动 + 启停钩子，切完提示重启
  const dfT2 = $('#driverFreeToggle');
  if (dfT2 && hasBridge) {
    dfT2.onchange = async () => {
      const r = JSON.parse(await B.setDriverFree(dfT2.checked ? '1' : '0'));
      dfT2.checked = r.driverFree === true;
      const sw = $('#driverFreeSwitch');
      if (sw) sw.classList.toggle('on', dfT2.checked);
      toast(r.message, 9000);
      await refresh();
    };
  }

  // 语音输入法：点哪个就切到哪个（业务侧只在"映射仍是预设值"时自动改写，不覆盖自定义）
  document.querySelectorAll('.ime-seg button[data-ime]').forEach(btn => {
    if (!hasBridge) return;
    btn.onclick = async () => {
      const r = JSON.parse(await B.setImeProfile(btn.dataset.ime));
      toast(r.message, 6000);
      await refresh();
    };
  });

  $('#btnTestInject').onclick = async () => {
    const r = JSON.parse(await B.testInject());
    toast(r.ok ? `注入正常：按下时系统读到「${r.name} 按下」，松开后已释放` : '注入异常，请看状态面板', 5000);
  };
  // 一键安装 / 修复：直接跑 -Force（清空旧节点再重建），**不弹黑窗口**、结果回到界面
  const doRepairDriver = async () => {
    if (!confirm('将重建虚拟键盘驱动（会先清空旧的 WinUHid 设备节点）。\n\n' +
                 '· 本程序已以管理员运行，不会再弹 UAC\n· 装完可能需要重启\n\n继续？')) return;
    toast('正在重建驱动，请稍候（约 5~15 秒）…', 60000);
    const r = JSON.parse(await B.repairDriver('1'));
    const head = r.ok
      ? (r.needRestart ? '✅ 驱动已重建，需要重启后生效' : `✅ 驱动已就绪（接口 v${r.driverVersion}）`)
      : '❌ 修复未成功';
    toast(head, 12000);
    // 详情写到控制台 + 面板备注，便于复制给我们
    if (r.message) console.log('[repairDriver]\n' + r.message);
    await refresh();
  };
  $('#btnInstallUhid').onclick = doRepairDriver;
  // 一键诊断：跑只读 Status，写桌面 txt，并尝试复制到剪贴板
  $('#btnDriverDiag').onclick = async () => {
    const r = JSON.parse(await B.exportDriverDiag());
    let copied = false;
    try { await navigator.clipboard.writeText(r.text); copied = true; } catch (e) { }
    const tip = (r.advice && r.advice.length ? '建议：' + r.advice.join('；') + '\n' : '') +
                (r.path ? '已保存到桌面：' + r.path.split('\\').pop() : '未能写文件') +
                (copied ? '（已复制到剪贴板）' : '');
    toast(tip, 12000);
  };
  $('#btnExportUhid').onclick = async () => {
    const r = JSON.parse(await B.exportWinuhid());
    toast(r.message, 6000);
  };
  $('#btnSetupAll').onclick = async () => {
    const r = JSON.parse(await B.runSetup());
    toast(r.message, 9000); await refresh();
  };
  $('#btnInstallCable2').onclick = async () => {
    if (!confirm('安装 VB-CABLE 虚拟声卡？\n\n会解压官方驱动包并运行官方安装器（UAC），装完通常需要重启。')) return;
    const r = JSON.parse(await B.installCable());
    toast(r.message, 9000);
  };
  $('#btnInstallUhid2').onclick = async () => {
    if (!confirm('重建虚拟键盘驱动（先清空旧节点）？\n\n不会弹 UAC；装完可能需重启。')) return;
    toast('正在重建驱动，请稍候…', 60000);
    const r = JSON.parse(await B.repairDriver('1'));
    toast(r.ok ? (r.needRestart ? '✅ 已重建，需要重启' : '✅ 驱动已就绪') : '❌ 修复未成功', 12000);
    await refresh();
  };
  $('#btnSwitchDriver').onclick = async () => {
    if (!confirm('把遥控器的设备驱动换成「HID-compliant device」？\n\n效果：系统不再把它当键盘，按遥控器不会再打出 m。\n随时可用「还原成键盘驱动」恢复。')) return;
    const r = JSON.parse(await B.setDeviceDriver('false'));
    toast(r.message, 9000); await refresh();
  };
  $('#btnRevertDriver').onclick = async () => {
    if (!confirm('还原成 HID Keyboard Device？（会重新出现 m 输出）')) return;
    const r = JSON.parse(await B.setDeviceDriver('true'));
    toast(r.message, 9000); await refresh();
  };
  $('#btnAutoStart').onclick = async () => {
    const r = JSON.parse(await B.setAutoStart(String(!(state && state.autoStart))));
    toast(r.message, 4200); await refresh();
  };
  $('#btnClearLog').onclick = async () => {
    const r = JSON.parse(await B.clearKeyLog());
    toast(r.message, 3000);
  };
  $('#btnAutoRun').onclick = async () => {
    const r = JSON.parse(await B.setAutoRun(String(!(state && state.autoRun))));
    toast(r.message, 5000); await refresh();
  };
  $('#btnStartMin').onclick = async () => {
    const r = JSON.parse(await B.setStartMinimized(String(!(state && state.startMinimized))));
    toast(r.message, 5000); await refresh();
  };
  $('#btnAutoDrv').onclick = async () => {
    const r = JSON.parse(await B.setAutoSwitchDriver(String(!(state && state.autoSwitchDriver))));
    toast(r.message, 6000); await refresh();
  };
  $('#btnAutoMic').onclick = async () => {
    const r = JSON.parse(await B.setAutoKeepMic(String(!(state && state.autoKeepMic))));
    toast(r.message, 6000); await refresh();
  };
  $('#btnCheckMic').onclick = async () => {
    const r = JSON.parse(await B.checkCaptureDevice());
    toast(r.message, r.canOpen && r.isCable ? 6000 : 14000);
    await refresh();
  };
  // 测试音：一键区分"链路没声"还是"输入法选错设备"
  const tt = $('#btnTestTone');
  if (tt) tt.onclick = async () => {
    const r = JSON.parse(await B.testTone());
    toast(r.message, 9000);
  };
  // 手动把默认录音设备设为 CABLE Output（不再自动抢）
  const setMic = async () => {
    const r = JSON.parse(await B.setDefaultMic());
    toast(r.message, 8000);
    await refresh();
  };
  // 两个面板各有一个同名动作（环境自检 / 虚拟声卡），id 必须不同，各自绑定
  ['#btnSetMic', '#btnSetMicCable'].forEach(sel => {
    const el = $(sel); if (el) el.onclick = setMic;
  });
  // ── 屏幕键盘弹窗：保存 / 不执行 / 取消 ──
  const curKey = () => ((state && state.keys && state.keys.items) || []).find(x => x.id === selectedKey);
  const comboVks = () => {
    if (kbKey === null && kbMods.size === 0) return [];           // 不执行
    return [...kbMods, ...(kbKey === null ? [] : [kbKey])];
  };
  $('#kbSave').onclick = async () => {
    const it = curKey(); if (!it) return;
    const v = comboVks();
    const r = JSON.parse(await B.saveKey(it.id, v.join(',')));
    toast(r.message, 5000);
    closeCombo(); await refresh();
  };
  $('#kbNone').onclick = () => { kbMods = new Set(); kbKey = null; kbKeyLabel = ''; paintKb(); };
  $('#kbCancel').onclick = closeCombo;
  // ★ 关闭路径（旧版漏了这三个 → 用户报「点击关闭无法关闭」）：
  //   ① 右上角 ×   ② 点遮罩空白处   ③ Esc
  $('#comboClose').onclick = closeCombo;
  const mask = $('#comboMask');
  if (mask) mask.onclick = (e) => { if (e.target === mask) closeCombo(); };
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && !$('#comboMask').classList.contains('hide')) closeCombo();
  });

  // ── 配置组：新建 / 删除 / 导出 / 导入 ──
  $('#gNew').onclick = async () => {
    const r = JSON.parse(await B.newGroup()); toast(r.message, 4000); await refresh();
  };
  $('#gDel').onclick = async () => {
    const r = JSON.parse(await B.deleteGroup(String((state.groups || {}).active || 0)));
    toast(r.message, 4000); await refresh();
  };
  $('#btnExport').onclick = async () => {
    const txt = await B.exportGroup();
    try {
      const a = document.createElement('a');
      a.href = URL.createObjectURL(new Blob([txt], { type: 'application/json' }));
      a.download = 'iprc1000-keymap.json'; a.click();
      toast('已导出当前配置组', 4000);
    } catch (e) { toast('导出失败：' + e.message, 5000); }
  };
  $('#btnImport').onclick = () => {
    const inp = document.createElement('input'); inp.type = 'file'; inp.accept = '.json';
    inp.onchange = () => {
      const f = inp.files && inp.files[0]; if (!f) return;
      const rd = new FileReader();
      rd.onload = async () => {
        const r = JSON.parse(await B.importGroup(String(rd.result)));
        toast(r.message, 6000); await refresh();
      };
      rd.readAsText(f);
    };
    inp.click();
  };

  // ── 搜索框：按 按键名 / HID / 映射动作 过滤 ──
  $('#gSearch').oninput = () => { if (state) renderKeys(state); };

  $('#btnReleaseAll').onclick = async () => {
    const r = JSON.parse(await B.releaseAllKeys());
    toast(r.message, 6000); await refresh();
  };
  $('#btnAdmin').onclick = async () => {
    const r = JSON.parse(await B.relaunchAdmin());
    toast(r.message, 4200);
  };
}

/* ───────── 桥接层：同时支持 WebView2（.NET 版）与 QWebChannel（PySide6 版） ───────── */

function makeWebView2Bridge() {
  let id = 0;
  const pend = new Map();
  window.chrome.webview.addEventListener('message', (e) => {
    const m = e.data;
    const p = pend.get(m && m.id);
    if (p) { pend.delete(m.id); p(m.result); }
  });
  const call = (method, args) => new Promise((res) => {
    const i = ++id;
    pend.set(i, res);
    window.chrome.webview.postMessage({ id: i, method, args });
  });
  return new Proxy({}, { get: (_, m) => (...a) => call(m, a) });
}

function hasWebView2() {
  return !!(window.chrome && window.chrome.webview && window.chrome.webview.postMessage);
}

/* ───────── 启动 ───────── */
function boot() {
  // 初始页标记：CSS 里按页做差异（body[data-page="keys"] …）依赖它，
  // 启动时不一定走 goto()，所以这里先落一个默认值。
  document.body.dataset.page = (new URLSearchParams(location.search).get('page')) || 'overview';
  const qp = new URLSearchParams(location.search).get('page');

  if (hasWebView2()) {
    B = makeWebView2Bridge();
    bindUI();
    refresh();
    if (qp) goto(qp);
    timer = setInterval(refresh, 2000);
    return;
  }

  if (typeof QWebChannel === 'undefined' || !window.qt) {
    /* ── 浏览器预览模式 ──
       直接用浏览器打开 ui/index.html 即可看版式（无需后端）。
       下面是一份**部署完成后的典型快照**：数据与 Defaults.Plain() 的出厂映射一致，
       缩略图坐标取自 Keys.Rects，所以预览里看到的就是真实界面。
       加 ?page=overview|keys|env 可直接跳到某一页（截图/自检用）。 */
    const demo = {
      admin: true,
      // 预览用的示例版本号 —— 真实值来自 csproj 的 <Version>（Env.AppVersion）。
      // 改版本时顺手同步这里即可（仅影响"直接用浏览器打开 ui/index.html"的预览与截图）。
      version: '1.2.111',
      buildStamp: '2026-10-16 09:50',
      versionLine: 'v1.2.111（构建于 2026-10-29 16:40）',
      device: {
        connected: true, paired: true, lastKey: '确认', lastState: '已翻译',
        detail: { hid: [{ id: 'HID\\{00001124-...}_VID&00020a5c_PID&8502', online: true }],
                  bthenum: [{ id: 'BTHENUM\\Dev_20C0470A3483', online: true }], paired: true },
      },
      keys: { configured: 22, total: 23, items: [] },
      reg: { EtwDropLargeEvents: 0, EtwLogSensitiveData: 1, MaxEtwBytes: 8192, ok: true,
             needReboot: false, boot: '2026-10-14 09:12', regMtime: '2026-10-13 15:30' },
      cable: { input: true, output: true, ready: true, defaultMic: true, version: '4.5' },
      bridge: { running: true, zombie: false,
                frames: 18422, samples: 5895040, seconds: 368.4,
                silentMs: 180, sessions: 3, gaps: 1, maxGapMs: 820,
                lostCont: 0, truncatedFirst: 0, udpPackets: 18422, uptimeSec: 421,
                seqJumps: 0, seqLost: 0, seqDup: 0, truncCont: 0, oddFrag: 0, hdrOdd: 0,
                orphanCont: 0, shortPdu: 0,
                hint: '正在监听 BthPort ETW（180 ms 前收到音频）' },
      inject: true,
      router: { online: true, packets: 1152, bytes: 4423680, error: null, session: true,
                jitter: { preRollMs: 40, capMs: 64, staleMs: 400, latencyMs: 30,
                          backlogMs: 44.0, peakBacklogMs: 63, playing: true,
                          underruns: 3, droppedBytes: 1920, staleResets: 1, primings: 5 },
                device: 'CABLE Input (VB-Audio Virtual Cable)' },
      swallow: { running: true, deviceSeen: true, swallowed: 128, passed: 46,
                 lastButton: '语音', pressed: true, spuriousLeaked: 0 },
      keysource: {
        mode: 'etw', stillKeyboard: false, logPath: 'key-events.log',
        etw: { online: true, port: 31681, events: 512, lastButton: '语音', lastUsage: '0x10',
               pressed: false, avgLatencyMs: 13, maxLatencyMs: 41, latencySamples: 480, error: null },
      },
      winuhid: { ready: true, driverVersion: 1, dllFound: true, error: null },
      setup: { ready: true,
        cable: { ok: true }, mic: { ok: true }, winuhid: { ok: true },
        device: { ok: true }, bridge: { ok: true } },
      stuckGuard: { watchdogMs: 800, unlockCount: 0, lastUnlock: null },
      mouse: { mode: 'mouse', enabled: true, held: [], gain: 1, accelSuspected: false,
               tickHz: 60, minPxPerSec: 60, maxPxPerSec: 900, rampMs: 700, rampExponent: 2,
               normalizeDiagonal: true, directionHoldTimeoutSec: 8, buttonHoldTimeoutSec: 10,
               rememberMode: false, calibrate: true,
               wheelOnVolumeKeys: false, wheelMinPerSec: 3, wheelMaxPerSec: 16,
                              frames: 0, pixelsMoved: 0, dirTimeouts: 0, btnTimeouts: 0, orphanReaps: 0,
               lastReap: null, note: null },
      autoStart: true, autoRun: true, startMinimized: true, autoSwitchDriver: true,
      autoKeepMic: true,
      defaultMicName: 'CABLE Output (VB-Audio Virtual Cable)',
      driverNote: '设备已是非键盘模式',
      groups: { active: 1, items: [
        { name: '默认配置组 1', kind: 'plain', configured: 22 },
        { name: '鼠标模式', kind: 'mouse', configured: 19 },
        { name: 'WorkBuddy 快捷键', kind: 'workbuddy', configured: 21 } ] },
    };
    // [id, 遥控器标注, 中文名, HID, 注入的组合键, 缩略图中心x, 中心y, 宽, 高]
    const rows = [
      ['power','pwr','电源','0x17','Win+L',141,38,44,46],   // 校正：圆心实测 (141,38)
      ['back','back','返回','0x09','Backspace',39,147,44,44],
      ['menu','menu','菜单','0x11','菜单键',139,147,44,44],
      ['exit','exit','退出 / Home','0x07','Esc',39,252,44,44],
      ['info','info','信息','0x0b','Ctrl+enter',139,252,44,44],
      ['ok','ok','确认','0x28','enter',91,200,48,44],
      ['up','▲','上','0x52','↑',91,147,48,44],
      ['down','▼','下','0x51','↓',91,252,48,44],
      ['left','‹','左','0x50','←',39,200,44,44],
      ['right','›','右','0x4f','→',139,200,44,44],
      ['favorite','♡','收藏','0x08','Win+Shift+S',39,318,44,44],
      ['voice','🎤','语音','0x10','RAlt',91,318,48,44],
      ['account','👤','账户','0x0d','Win+E',139,318,44,44],
      ['play_pause','⏯','播放 / 暂停','0x31','播放/暂停',91,372,48,44],
      ['fast_forward','▶▶','快进','0x19','Alt+Esc',139,372,44,44],
      ['rewind','◀◀','快退','0x36','Win+D',39,372,44,44],
      ['channel_up','ch▲','频道 +','0x04','PgUp',142,428,46,30],
      ['channel_down','ch▼','频道 -','0x05','PgDn',142,478,46,30],
      ['volume_up','vol+','音量 +','0x1f','音量+',39,428,46,30],
      ['volume_down','vol−','音量 -','0x20','音量-',39,478,46,30],
      ['mute','🔇','静音','0x0a','静音',91,428,42,42],
      ['last','last','上一频道','0x06','Alt+Tab',105,478,48,42],
      ['square','▭','方块键','0x16','切换配置组',90,617,46,36],   // 出厂值 = 动作值「切换配置组」
    ];
    const IMG = { w: 179, h: 663 };
    demo.keys.items = rows.map(([id, label, name, hid, sym, cx, cy, w, h]) => ({
      id, name, label, hid, cap: label, sym, vks: [],
      binding: sym, rect: { cx, cy, w, h }, img: IMG,
    }));
    state = demo;
    renderOverview(demo); renderKeys(demo); renderEnv(demo); renderMouse(demo);
    if (qp) goto(qp);
    // ?modal=<按键id> → 预览时直接弹出该键的屏幕键盘（截图用，如 ?page=keys&modal=ok）
    const md = new URLSearchParams(location.search).get('modal');
    if (md) { try { selectKey(md); } catch (_) {} }
    return;
  }
  new QWebChannel(qt.webChannelTransport, (ch) => {
    B = ch.objects.backend;
    bindUI();
    refresh();
    if (qp) goto(qp);
    timer = setInterval(refresh, 2000);
  });
}
window.addEventListener('load', boot);
