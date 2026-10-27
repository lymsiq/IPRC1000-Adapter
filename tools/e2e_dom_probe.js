/**
 * DOM 探针：连真实界面，输出关键区域的 id/class 结构，用于定位测试选择器与实际布局。
 * 用法：node tools/e2e_dom_probe.js
 */
const path = require('path');
const { chromium } = require(path.join(
  process.env.NODE_PATH || 'C:/Users/Administrator/.workbuddy/binaries/node/workspace/node_modules',
  'playwright-core'));

(async () => {
  const b = await chromium.connectOverCDP('http://127.0.0.1:9222');
  const ctx = b.contexts()[0];
  let page = null;
  for (let i = 0; i < 30; i++) {
    page = ctx.pages().find(p => p.url().includes('index.html'));
    if (page) break;
    await new Promise(r => setTimeout(r, 500));
  }
  if (!page) { console.log('没找到页面'); process.exit(2); }
  await page.waitForTimeout(1200);

  const dump = await page.evaluate(() => {
    const out = {};
    // 1) 门槛输入框 / 搜索范围下拉在哪
    out.numberInputs = [...document.querySelectorAll('input[type=number]')]
      .map(e => ({ id: e.id, parentId: e.parentElement?.id, parentCls: e.parentElement?.className,
                   inDialog: !!e.closest('.modal, #comboMask, #kbMouse') }));
    out.selects = [...document.querySelectorAll('select')]
      .map(e => ({ id: e.id, parentId: e.parentElement?.id, inDialog: !!e.closest('.modal, #comboMask') }));
    // 2) 按键行用什么容器
    out.keyRowContainers = [...document.querySelectorAll('[id]')]
      .filter(e => /kb|key/i.test(e.id))
      .map(e => ({ id: e.id, tag: e.tagName, cls: e.className, kids: e.children.length }))
      .slice(0, 14);
    // 2.5) 配置组 Tab 的实际结构与高亮类名
    const gt = document.querySelector('#gTabs');
    out.gTabs = gt ? {
      html: gt.outerHTML.slice(0, 500),
      children: [...gt.children].map(c => ({ tag: c.tagName, cls: c.className, text: (c.textContent || '').slice(0, 12) })),
      activeByOn: [...document.querySelectorAll('#gTabs .on')].length,
      activeByActive: [...document.querySelectorAll('#gTabs .active')].length,
    } : null;
    // 3) 按键页里第一屏的可点击行
    const keysPage = document.querySelector('#page-keys');
    out.keysPageClickable = keysPage ? [...keysPage.querySelectorAll('[onclick], .keyrow, tr, li')]
      .slice(0, 6).map(e => ({ tag: e.tagName, id: e.id, cls: e.className })) : [];
    return out;
  });
  console.log(JSON.stringify(dump, null, 2));
  await b.close().catch(() => {});
})();
