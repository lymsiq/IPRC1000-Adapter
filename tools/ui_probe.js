/* IPRC1000 Adapter — 界面冒烟探针（真实浏览器渲染）
 *
 * 为什么需要它：
 *   2026-10-15 出过一次「白屏」事故 —— 用静态分析（grep id/class）判断“未引用”后误删了
 *   page-* 的 id，导致每一页都被加上 hidden。当时**看不到界面**，只能靠猜，改错一轮才发现。
 *   这个探针把“界面到底渲染出来没有”变成可自动断言的事实。
 *
 * 做法：
 *   ui/app.js 自带「浏览器预览模式」——检测不到 WebView2 桥且没有 QWebChannel 时，
 *   会拿一份 demo 快照直接把三页渲染出来。所以不需要任何后端就能真实跑通 UI。
 *
 * 它断言的（任一不过即 exit 1）：
 *   1. 页面加载无 JS 未捕获异常
 *   2. 启动后**恰好一页可见**（不是 0 页 —— 0 页就是白屏）
 *   3. 可见页有实际高度（>200px）且含有可见文本（防“有 DOM 但渲染成空”）
 *   4. 三个导航按钮逐个点：目标页可见、其余隐藏、按钮 active 态正确
 *   5. 遥控器图片与每个缩略图都真的加载成功（naturalWidth > 0）
 *   6. 组合键弹窗能打开且内容非空
 *
 * 用法：
 *   NODE_PATH=<managed node workspace>/node_modules node tools/ui_probe.js [--out 目录] [--keep]
 *   退出码 0 = 全过；非 0 = 有断言失败（可直接进 CI）
 */
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');
const { chromium } = require('playwright-core');

const ROOT = path.resolve(__dirname, '..');
const UI = path.join(ROOT, 'iprc1000_gui_dotnet', 'ui');
// 2026-10-29：鼠标参数并入环境设置后，鼠标页已不存在，只剩三页
const PAGES = ['overview', 'keys', 'env'];
// 没有侧栏导航、但仍存在的页面（从界面内部跳进去）——
// 2026-10-29：鼠标模式开关并入环境设置后，侧栏不再列「鼠标模式」，
// 但 #page-mouse（手感/曲线/自校准）仍保留，由环境设置里的按钮 goto 进去。
const HIDDEN_PAGES = [];   // 保留机制：将来若出现"有页面、无侧栏导航"再往里加

const argv = process.argv.slice(2);
const outIdx = argv.indexOf('--out');
const OUT = outIdx >= 0 ? path.resolve(argv[outIdx + 1]) : path.join(ROOT, '_ui_probe');
const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';

const fails = [];
const notes = [];
function ok(msg) { notes.push('  ✓ ' + msg); }
function bad(msg) { fails.push(msg); notes.push('  ✗ ' + msg); }

/* 页面里跑的一段求值：报告“可见性真相” */
const PROBE = () => {
  const vis = (el) => {
    if (!el) return false;
    const cs = getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden' || cs.opacity === '0') return false;
    const r = el.getBoundingClientRect();
    return r.width > 1 && r.height > 1;
  };
  const pages = [...document.querySelectorAll('.page')];
  const shown = pages.filter(vis);
  const main = document.querySelector('main');
  // 缩略图：按键列表每行是 .keythumb，用 remote.png 当 sprite（背景图，不是 <img>）
  const thumbs = [...document.querySelectorAll('#keyList .keythumb')];
  const rail = document.querySelector('.rail img');
  return {
    pageCount: pages.length,
    pageIds: pages.map((p) => p.id),
    visibleCount: shown.length,
    visibleIds: shown.map((p) => p.id),
    visibleText: shown.map((p) => p.innerText.trim().length),
    visibleHeight: shown.map((p) => Math.round(p.getBoundingClientRect().height)),
    mainHeight: main ? Math.round(main.getBoundingClientRect().height) : 0,
    bodyTextLen: document.body.innerText.trim().length,
    activeNav: [...document.querySelectorAll('.nav-btn')].filter((b) => b.classList.contains('active')).map((b) => b.dataset.page),
    navPages: [...document.querySelectorAll('.nav-btn')].map((b) => b.dataset.page),
    railImgOk: !!(rail && rail.naturalWidth > 0),
    thumbCount: thumbs.length,
    // 背景图是否真的指到了 remote.png（sprite 裁切）
    thumbsOk: thumbs.filter((e) => /remote\.png/.test(getComputedStyle(e).backgroundImage)).length,
    railVer: (document.getElementById('railVer') || {}).textContent || '',
    // ★ 发布版默认不写日志（v1.2.16）：界面不该出现任何"日志"字样，
    //   否则用户会以为程序还在写日志（2026-10-17 用户截图指出的正是这个）
    hasLogUi: /按键日志|清空按键日志/.test(document.body.innerText || ''),
    // ★ 幽灵帧 0x0C 曾经把"最近按键"污染成「voice（usage 0x0C）」这种错配
    ghostMismatch: /（usage 0x0C/.test(document.body.innerText || ''),
    // ★ 右侧快捷键必须和"名字"那一行对齐。曾经相对两行块居中 ⇒ 比名字低 8px，看着像"错位"
    valAlignDelta: [...document.querySelectorAll('#keyList .keyrow')].slice(0, 6).reduce((mx, row) => {
      const n = row.querySelector('.keyname'), v = row.querySelector('.keybind-sym');
      if (!n || !v) return mx;
      const nb = n.getBoundingClientRect(), vb = v.getBoundingClientRect();
      return Math.max(mx, Math.abs((vb.y + vb.height / 2) - (nb.y + nb.height / 2)));
    }, 0),
  };
};

(async () => {
  if (!fs.existsSync(path.join(UI, 'index.html'))) {
    console.error('找不到 ui/index.html：' + UI);
    process.exit(1);
  }
  fs.mkdirSync(OUT, { recursive: true });

  const browser = await chromium.launch({ headless: true, executablePath: EDGE });
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, locale: 'zh-CN', deviceScaleFactor: 1 });
  const page = await ctx.newPage();

  const jsErrors = [];
  const consoleErrors = [];
  page.on('pageerror', (e) => jsErrors.push(e.message));
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const t = m.text();
    // “Failed to load resource”看不到 URL，交给下面的 requestfailed 处理，这里跳过避免重复
    if (/Failed to load resource/i.test(t)) return;
    consoleErrors.push(t);
  });
  page.on('requestfailed', (r) => {
    const u = r.url();
    // index.html 里那段桥探测脚本会试探 qwebchannel.js 与 qrc:///（预览模式下必然失败）——预期噪声
    if (/qwebchannel|qrc:%2F%2F%2F|qrc:\/\//i.test(u)) return;
    consoleErrors.push('资源加载失败: ' + u);
  });

  const url = pathToFileURL(path.join(UI, 'index.html')).href;
  await page.goto(url, { waitUntil: 'load' });
  await page.waitForTimeout(700);                  // 等 render + 图片解码

  /* ── 1. 启动态 ── */
  let s = await page.evaluate(PROBE);
  console.log('启动态:', JSON.stringify({ visibleIds: s.visibleIds, navPages: s.navPages, pageIds: s.pageIds }));

  if (jsErrors.length) bad('JS 未捕获异常 ' + jsErrors.length + ' 条：' + jsErrors[0]);
  else ok('无 JS 未捕获异常');

  if (s.pageCount !== PAGES.length) bad('页面容器数量应为 ' + PAGES.length + '，实为 ' + s.pageCount);
  else ok('页面容器 ' + s.pageCount + ' 个');

  const missingIds = PAGES.filter((p) => !s.pageIds.includes('page-' + p));
  if (missingIds.length) bad('缺少页面 id：' + missingIds.map((p) => 'page-' + p).join(', ') + '（goto() 按 page- 前缀拼接找页，缺一个就会全页隐藏）');
  else ok('页面 id 齐全（' + PAGES.map((p) => 'page-' + p).join(' / ') + '）');

  const badNav = PAGES.filter((p) => !HIDDEN_PAGES.includes(p) && !s.navPages.includes(p));
  if (badNav.length) bad('导航按钮缺 data-page：' + badNav.join(', '));
  else ok('导航按钮 data-page 齐全');

  if (s.visibleCount === 0) bad('★ 白屏：一页都没显示（visibleCount=0）');
  else if (s.visibleCount > 1) bad('同时显示了 ' + s.visibleCount + ' 页：' + s.visibleIds.join(', '));
  else ok('恰好 1 页可见：' + s.visibleIds[0]);

  if (s.visibleCount === 1) {
    if (s.visibleHeight[0] < 200) bad('可见页高度仅 ' + s.visibleHeight[0] + 'px（<200，疑似渲染为空）');
    else ok('可见页高度 ' + s.visibleHeight[0] + 'px');
    if (s.visibleText[0] < 30) bad('可见页文本仅 ' + s.visibleText[0] + ' 字（<30，疑似内容没渲染）');
    else ok('可见页文本 ' + s.visibleText[0] + ' 字');
  }

  if (s.railImgOk) ok('侧栏遥控器图已加载');
  else bad('侧栏遥控器图未加载（remote.png）');

  await page.screenshot({ path: path.join(OUT, '01-boot.png'), fullPage: true });

  /* ── 1.5 补上事件绑定 ──
     预览模式（无 WebView2 桥）下 boot() 只渲染、**不调用 bindUI()**，所以导航按钮没有 onclick。
     这里显式补一次，走的就是生产代码里那条 `b.onclick = () => goto(b.dataset.page)` 的路径。
     bindUI 里没有“绑定那一刻就调后端”的语句，所以预览模式下调用是安全的。 */
  let bound = false;
  try {
    bound = await page.evaluate(() => {
      if (typeof bindUI !== 'function') return false;
      bindUI();
      const nb = [...document.querySelectorAll('.nav-btn')];
      return nb.length > 0 && nb.every((b) => typeof b.onclick === 'function');
    });
  } catch (e) {
    bad('bindUI() 抛异常：' + e.message);
  }
  if (bound) ok('导航按钮已绑定 onclick（bindUI 正常完成）');
  else bad('导航按钮没有 onclick —— 点了不会切页');

  /* ── 1.6 app.js 引用的每个元素都必须存在 ──
     直接从源码抓 $('#xxx') / getElementById('xxx')，再到 DOM 里逐个核对。
     这道闸防的是「改 HTML 时删掉了 JS 还在用的元素」——一旦发生，
     对应功能在真机上会静默失效或直接抛 TypeError。 */
  {
    const src = fs.readFileSync(path.join(UI, 'app.js'), 'utf8')
      // 先剥注释再抓 id，否则注释里的示例（如 `$('#x')`）会算进去
      .replace(/\/\*[\s\S]*?\*\//g, ' ')
      .replace(/(^|[^:])\/\/[^\n]*/g, '$1');   // [^:] 是为了不吃掉字符串里的 https://
    const ids = new Set();
    for (const m of src.matchAll(/\$\('#([A-Za-z][\w-]*)'\)/g)) ids.add(m[1]);
    for (const m of src.matchAll(/getElementById\('([A-Za-z][\w-]*)'\)/g)) ids.add(m[1]);
    const inDom = await page.evaluate((list) => list.map((id) => [id, !!document.getElementById(id)]), [...ids]);
    const missing = inDom.filter(([, f]) => !f).map(([id]) => id);
    if (missing.length) bad('app.js 引用但 DOM 里不存在的元素：' + missing.join(', '));
    else ok('app.js 引用的 ' + ids.size + ' 个元素在 DOM 里全部存在');
  }

  /* ── 2. 逐页切换 ── */
  for (const name of PAGES) {
    if (HIDDEN_PAGES.includes(name)) {
      await page.evaluate((n) => window.goto ? window.goto(n) : null, name);
    } else {
      await page.click(`.nav-btn[data-page="${name}"]`);
    }
    await page.waitForTimeout(320);
    const t = await page.evaluate(PROBE);
    const want = 'page-' + name;
    if (t.visibleCount !== 1) bad('切到 ' + name + ' 后可见页数 = ' + t.visibleCount + '（应为 1）');
    else if (t.visibleIds[0] !== want) bad('切到 ' + name + ' 后显示的是 ' + t.visibleIds[0]);
    else if (t.visibleHeight[0] < 200) bad('切到 ' + name + ' 后高度仅 ' + t.visibleHeight[0] + 'px');
    else ok('切到 ' + name + ' 正常（' + t.visibleHeight[0] + 'px / ' + t.visibleText[0] + ' 字）');
    // 无侧栏导航的页面（HIDDEN_PAGES）本来就没有按钮可高亮，跳过这条
    if (HIDDEN_PAGES.includes(name)) {
      if (t.activeNav.length === 0) ok('  ' + name + ' 无侧栏导航（按设计）');
      else bad('  ' + name + ' 不应有导航高亮：' + JSON.stringify(t.activeNav));
    } else if (t.activeNav.length === 1 && t.activeNav[0] === name) ok('  ' + name + ' 导航高亮正确');
    else bad('  ' + name + ' 导航高亮异常：' + JSON.stringify(t.activeNav));
    // ★ 发布版默认不写日志：任何页面上都不该出现"按键日志/清空按键日志"
    if (t.hasLogUi) bad('  ' + name + ' 页面仍出现"日志"字样（发布版默认不写日志，应隐藏）');
    // ★ 幽灵帧不许污染"最近按键"（曾出现「voice（usage 0x0C）」这种错配）
    if (t.ghostMismatch) bad('  ' + name + ' 页面出现"（usage 0x0C" —— 幽灵帧污染了最近按键');
    await page.screenshot({ path: path.join(OUT, `02-page-${name}.png`), fullPage: true });

    if (name === 'keys') {
      // 缩略图用 remote.png 当 sprite：数量应等于按键数，且 background-image 指得到图
      // ★ 右侧快捷键与名字同一行（偏差 ≤1px）
      if (t.valAlignDelta > 1) bad('按键行右值未与名字对齐：偏差 ' + t.valAlignDelta.toFixed(1) + ' px');
      else ok('按键行右值与名字对齐（偏差 ' + t.valAlignDelta.toFixed(1) + ' px）');
      if (t.thumbCount === 0) bad('按键列表里没有 .keythumb 缩略图');
      else if (t.thumbsOk < t.thumbCount) bad('缩略图背景图未指向 remote.png：' + t.thumbsOk + '/' + t.thumbCount);
      else ok('按键缩略图全部就位（' + t.thumbsOk + '/' + t.thumbCount + ' 个 sprite）');
      // 6. 弹窗
      const row = await page.$('#keyList .keyrow');
      if (row) {
        await row.click();
        await page.waitForTimeout(320);
        const m = await page.evaluate(() => {
          const el = document.getElementById('comboMask');
          const cs = el ? getComputedStyle(el) : null;
          const open = !!el && cs.display !== 'none' && !el.classList.contains('hide');
          const body = document.querySelector('#comboModal .modal-body');
          const ks = document.querySelectorAll('#kbMain .kb-key, #kbExtra .kb-key').length;
          const mods = document.querySelectorAll('#kbMods .m').length;
          return {
            open,
            bodyText: body ? body.innerText.trim().length : 0,
            kbKeys: ks,
            mods,
            cur: (document.getElementById('comboCur') || {}).textContent || '',
          };
        });
        if (!m.open) bad('组合键弹窗没打开');
        else if (m.bodyText < 10) bad('弹窗内容为空（' + m.bodyText + ' 字）');
        else if (m.kbKeys < 30) bad('屏幕键盘按键过少：主键区 ' + m.kbKeys + ' 个');
        else ok('组合键弹窗正常（' + m.kbKeys + ' 键 / ' + m.mods + ' 修饰键 / 当前=' + m.cur + '）');
        await page.screenshot({ path: path.join(OUT, '03-modal.png') });

        // ★ 2026-10-30：弹窗三页签（按键 / 文本 / 宏）
        //   这是新增功能最容易坏的地方：页签切了但面板显隐没跟上、文本域塌成 0 高、
        //   加步骤按钮没绑上、底部说明串页签。全部用断言钉住。
        const tp = await page.evaluate(() => {
          const r = {};
          r.tabs = [...document.querySelectorAll('#comboTabs .cm-tab')].map((b) => b.dataset.tab);
          switchComboTab('text');
          r.textVisible = !document.getElementById('paneText').classList.contains('hide');
          r.keyHidden = document.getElementById('paneKey').classList.contains('hide');
          r.slots = document.querySelectorAll('#txtSlots .cm-slot').length;
          r.textH = Math.round(document.getElementById('txtBody').getBoundingClientRect().height);
          r.noteText = (document.getElementById('comboNote').textContent || '').trim().slice(0, 6);
          switchComboTab('macro');
          r.macroVisible = !document.getElementById('paneMacro').classList.contains('hide');
          r.hasRec = !!document.getElementById('recBtn');
          r.hasLua = !!document.getElementById('luaBtn');
          r.hasClear = !!document.getElementById('macClear');
          r.hasGlobal = !!document.getElementById('comboGlobal');
          document.querySelector('#macAdd button[data-add="keys"]').click();
          document.querySelector('#macAdd button[data-add="wait"]').click();
          r.steps = document.querySelectorAll('#macSteps .cm-step').length;
          r.noteMacro = (document.getElementById('comboNote').textContent || '').trim().slice(0, 6);

          // 失败策略：点一下应在「停 / 跳」之间切（默认停）
          const of0 = document.querySelector('#macSteps .of');
          r.ofBefore = of0 ? of0.textContent.trim() : '';
          if (of0) of0.click();
          const of1 = document.querySelector('#macSteps .of');
          r.ofAfter = of1 ? of1.textContent.trim() : '';

          // 粘贴步骤应有**两个**下拉：文本槽位 + 粘贴键（终端要 Ctrl+Shift+V）
          document.querySelector('#macAdd button[data-add="paste"]').click();
          const pasteRow = [...document.querySelectorAll('#macSteps .cm-step')]
            .find((x) => (x.querySelector('.ty') || {}).textContent === '粘贴');
          r.pasteSelects = pasteRow ? pasteRow.querySelectorAll('select').length : 0;

          document.querySelector('#macSteps .cm-step .del').click();
          r.stepsAfterDel = document.querySelectorAll('#macSteps .cm-step').length;
          // 循环遍数：默认必须是 1（触发一次），设成 0 时底部要明确写"一直循环"
          const lp = document.getElementById('macLoop');
          r.loopDefault = lp ? lp.value : '(缺)';
          if (lp) { lp.value = 0; if (lp.onchange) lp.onchange(); }
          r.footLoop = (document.getElementById('comboCur').textContent || '').trim();
          switchComboTab('key');
          r.backKeys = document.querySelectorAll('#kbMain .kb-key, #kbExtra .kb-key').length;
          return r;
        });
        if (tp.tabs.join(',') !== 'key,text,macro') bad('弹窗页签不齐：' + tp.tabs.join(','));
        else if (!tp.textVisible || !tp.keyHidden) bad('切到「文本」页签时面板显隐不对');
        else if (tp.slots !== 10) bad('文本槽位数不是 10：' + tp.slots);
        else if (tp.textH < 80) bad('文本输入框太矮（' + tp.textH + 'px），可能没渲染出来');
        else if (!tp.macroVisible || tp.steps !== 2 || tp.stepsAfterDel !== 2)
          bad('宏步骤增删异常：加 3 步后删 1 步，期望 2 步，实得 ' + tp.stepsAfterDel);
        else if (!tp.hasRec) bad('宏页签缺少「开始录制」按钮');
        else if (!tp.hasLua) bad('宏页签缺少「导入 .lua」按钮');
        else if (!tp.hasClear) bad('宏页签缺少「清空」按钮（重新配宏时只能逐条删）');
        else if (!tp.hasGlobal) bad('弹窗缺少「全局」开关');
        else if (tp.ofBefore !== '停' || tp.ofAfter !== '跳')
          bad('失败策略按钮不能切换：「' + tp.ofBefore + '」→「' + tp.ofAfter + '」');
        else if (tp.pasteSelects !== 2)
          bad('粘贴步骤的下拉不是 2 个（槽位 + 粘贴键），实得 ' + tp.pasteSelects);
        else if (tp.backKeys < 30) bad('切回「按键」页签后屏幕键盘不见了：' + tp.backKeys);
        else if (tp.noteText === tp.noteMacro)
          bad('底部说明没跟着页签变（文本与宏一样：「' + tp.noteText + '」）');
        else if (tp.loopDefault !== '1') bad('宏的「执行遍数」默认不是 1（是 ' + tp.loopDefault + '），误设会变成循环');
        else if (!/一直循环/.test(tp.footLoop)) bad('遍数设 0 时底部没提示"一直循环"：「' + tp.footLoop + '」');
        else ok('弹窗三页签正常（文本 ' + tp.slots + ' 槽 / 宏步骤增删 / 失败策略可切 / 粘贴键可选 / 可录制/导入/清空 / 全局开关 / 遍数默认 1）');

        await page.keyboard.press('Escape');
        await page.waitForTimeout(220);
        const closed = await page.evaluate(() => {
          const el = document.getElementById('comboMask');
          return getComputedStyle(el).display === 'none' || el.classList.contains('hide');
        });
        if (closed) ok('Esc 能关掉弹窗');
        else bad('Esc 关不掉弹窗');
      } else {
        bad('按键列表里找不到 .keyrow');
      }
    }
  }

  await browser.close();

  if (consoleErrors.length) bad('控制台错误 ' + consoleErrors.length + ' 条：' + consoleErrors.slice(0, 3).join(' | '));
  else ok('无控制台错误');

  console.log('\n── 探针结果 ──');
  notes.forEach((n) => console.log(n));
  console.log('\n截图目录：' + OUT);
  if (fails.length) {
    console.log('\n★ 失败 ' + fails.length + ' 项：');
    fails.forEach((f) => console.log('   - ' + f));
    process.exit(1);
  }
  console.log('\n界面冒烟探针：全部通过 ✓');
})().catch((e) => { console.error('探针自身出错:', e.message); process.exit(2); });
