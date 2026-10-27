/**
 * 实机点击测试（连真实运行的 WebView2，走完整链路：点击 → postMessage → C# → 设置 → 状态 → 界面）
 *
 * 与 tools/ui_probe.js 的分工：
 *   · ui_probe  = 打开 index.html 预览模式（无宿主桥），只验证**渲染与导航**；
 *   · 本脚本    = 连 CDP（IPRC1000_WEBVIEW_DEBUG=9222），验证**按钮点了之后是否真的生效**。
 *
 * 安全原则：
 *   · 只点**可逆**的东西（开关切换后切回、输入框改完改回）；
 *   · 绝不碰：装驱动 / 装声卡 / 改默认麦克风 / 停桥 / 切设备驱动 / 一键完成全部；
 *   · 结束时把改动全部还原。
 *
 * 用法：node tools/e2e_click_test.js [--port 9222]
 */
const path = require('path');
const { chromium } = require(path.join(
  process.env.NODE_PATH || 'C:/Users/Administrator/.workbuddy/binaries/node/workspace/node_modules',
  'playwright-core'));

const PORT = process.argv.includes('--port')
  ? process.argv[process.argv.indexOf('--port') + 1] : '9222';

const results = [];
function rec(name, ok, detail) {
  results.push({ name, ok, detail: detail || '' });
  console.log(`${ok ? '  ✓' : '  ✗'} ${name}${detail ? '  — ' + detail : ''}`);
}

(async () => {
  const browser = await chromium.connectOverCDP(`http://127.0.0.1:${PORT}`);
  const ctx = browser.contexts()[0];
  let page = null;
  for (let i = 0; i < 30; i++) {
    page = ctx.pages().find(p => p.url().includes('index.html'));
    if (page) break;
    await new Promise(r => setTimeout(r, 500));
  }
  if (!page) { console.log('  ✗ 没找到 index.html 页面'); process.exit(2); }
  console.log(`  已连接：${page.url().slice(-40)}`);

  const errors = [];
  page.on('pageerror', e => errors.push('pageerror: ' + e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push('console: ' + m.text()); });

  // 等首屏渲染（页面会先拉一次 getState）
  await page.waitForTimeout(1500);

  const visiblePages = () => page.evaluate(() =>
    [...document.querySelectorAll('section.page')]
      .filter(s => getComputedStyle(s).display !== 'none')
      .map(s => s.id));

  // ── 1) 导航：逐页切换，每页只应有一页可见 ──
  const navs = ['overview', 'keys', 'env', 'mouse'];
  let navOk = true, navDetail = [];
  for (const n of navs) {
    await page.click(`.nav-btn[data-page="${n}"]`);
    await page.waitForTimeout(250);
    const vis = await visiblePages();
    if (vis.length !== 1 || vis[0] !== `page-${n}`) { navOk = false; navDetail.push(`${n}→${vis}`); }
  }
  rec('导航 4 页切换', navOk, navOk ? '每页唯一可见' : navDetail.join(' '));

  // ── 2) 事件日志开关：点 → 界面反映 → 再点回 ──
  await page.click('.nav-btn[data-page="env"]');
  await page.waitForTimeout(400);
  const noteEl = page.locator('#noteToggle');
  const noteBefore = await noteEl.isChecked();
  await noteEl.click();
  await page.waitForTimeout(800);                 // 等一次 refresh（2s 轮询 + 响应）
  const noteAfter = await noteEl.isChecked();
  rec('事件日志开关（点击后不复位）', noteAfter !== noteBefore,
      `初始 ${noteBefore} → 点击后 ${noteAfter}`);
  if (noteAfter !== noteBefore) {                 // 还原
    await noteEl.click();
    await page.waitForTimeout(800);
    rec('事件日志开关（可还原）', (await noteEl.isChecked()) === noteBefore);
  }

  // ── 3) 诊断日志开关：同样验证能切回 ──
  const logEl = page.locator('#logToggle');
  const logBefore = await logEl.isChecked();
  await logEl.click();
  await page.waitForTimeout(800);
  const logAfter = await logEl.isChecked();
  rec('诊断日志开关（点击后不复位）', logAfter !== logBefore, `${logBefore} → ${logAfter}`);
  if (logAfter !== logBefore) { await logEl.click(); await page.waitForTimeout(800); }

  // ── 4) 截图点击：门槛输入框 / 搜索范围下拉，改完读回并还原 ──
  await page.click('.nav-btn[data-page="keys"]');
  await page.waitForTimeout(400);
  const th = page.locator('#clickMinScore');
  if (await th.count()) {
    const before = await th.inputValue();
    await th.fill('0.77'); await th.dispatchEvent('change');
    await page.waitForTimeout(700);
    const after = await th.inputValue();
    rec('截图点击门槛（可编辑并保持）', after === '0.77', `${before} → ${after}`);
    await th.fill(before); await th.dispatchEvent('change'); await page.waitForTimeout(500);
  } else rec('截图点击门槛（未找到输入框）', false);

  const sel = page.locator('#clickScope');
  if (await sel.count()) {
    const before = await sel.inputValue();
    const target = before === '2' ? '1' : '2';
    await sel.selectOption(target); await page.waitForTimeout(700);
    const after = await sel.inputValue();
    rec('搜索范围下拉（可切换并保持）', after === target, `${before} → ${after}`);
    await sel.selectOption(before); await page.waitForTimeout(500);
  } else rec('搜索范围下拉（未找到）', false);

  // ── 4.5) 搜索过滤：只读，无副作用 ──
  const search = page.locator('#gSearch');
  if (await search.count()) {
    const total = await page.locator('#keyList .keyrow').count();
    await search.fill('语音'); await page.waitForTimeout(400);
    const filtered = await page.locator('#keyList .keyrow').count();
    await search.fill(''); await page.waitForTimeout(400);
    const restored = await page.locator('#keyList .keyrow').count();
    rec('搜索框过滤按键', filtered > 0 && filtered < total && restored === total,
        `${total} → ${filtered} → ${restored}`);
  } else rec('搜索框（未找到）', false);

  // ── 4.6) 配置组切换：切走再切回（有活动组，属于可逆）──
  const tabCount = await page.locator('#gTabs .gtab').count();
  if (tabCount >= 2) {
    // 必须点**当前没激活**的那个，否则点了等于没点（之前就踩过：目标组恰好已激活）
    const activeIdx = await page.evaluate(() =>
      [...document.querySelectorAll('#gTabs .gtab')].findIndex(e => e.classList.contains('on')));
    const target = activeIdx === 0 ? 1 : 0;
    const nameOf = (i) => page.evaluate((k) =>
      (document.querySelectorAll('#gTabs .gtab')[k]?.textContent || '').trim(), i);
    const before = await nameOf(activeIdx);
    await page.locator('#gTabs .gtab').nth(target).click();
    await page.waitForTimeout(800);
    const nowIdx = await page.evaluate(() =>
      [...document.querySelectorAll('#gTabs .gtab')].findIndex(e => e.classList.contains('on')));
    const after = await nameOf(nowIdx);
    rec('配置组切换（点非激活组）', nowIdx === target, `${before} → ${after}`);
    // 切回原组
    await page.locator('#gTabs .gtab').nth(activeIdx).click();
    await page.waitForTimeout(700);
    const backIdx = await page.evaluate(() =>
      [...document.querySelectorAll('#gTabs .gtab')].findIndex(e => e.classList.contains('on')));
    rec('配置组切回原组', backIdx === activeIdx);
  } else rec('配置组（组数不足，跳过）', false);

  // ── 5) 键盘弹窗：点一行按键 → 弹窗出现 → Esc 关闭 ──
  const row = page.locator('#keyList .keyrow').first();
  if (await row.count()) {
    await row.click();
    await page.waitForTimeout(400);
    const opened = await page.evaluate(() => {
      const m = document.querySelector('#comboMask');
      return !!m && getComputedStyle(m).display !== 'none';
    });
    rec('按键行 → 屏幕键盘弹窗打开', opened);
    if (opened) {
      await page.keyboard.press('Escape');
      await page.waitForTimeout(300);
      const closed = await page.evaluate(() => {
        const m = document.querySelector('#comboMask');
        return !m || getComputedStyle(m).display === 'none';
      });
      rec('Esc 关闭弹窗', closed);
    }
  } else rec('按键行（未找到 .keyrow）', false);

  // ── 6) 全页面无 JS 异常 ──
  rec('无 JS 运行时错误', errors.length === 0, errors.slice(0, 3).join(' | '));

  // ── 7) 回到总览，确认最终状态正常 ──
  await page.click('.nav-btn[data-page="overview"]');
  await page.waitForTimeout(400);
  rec('回到总览页', (await visiblePages()).join() === 'page-overview');

  const bad = results.filter(r => !r.ok);
  console.log(`\n  合计 ${results.length} 项：通过 ${results.length - bad.length} · 失败 ${bad.length}`);
  await browser.close().catch(() => {});
  process.exit(bad.length ? 1 : 0);
})().catch(e => { console.log('  ✗ 测试脚本异常: ' + e.message); process.exit(2); });
