// boot.js —— 卡片区本地搜索监听 + 启动引导。最后加载。
// ---------- 卡片区本地搜索 ----------
let searchTimer = null;
$('search').addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => {
    const st = stack.length ? stack[stack.length - 1] : null;
    const v = st ? st.view : '';
    const kw = $('search').value.trim();
    if (v === 'detail' || v === 'files') return;
    // 媒体库根 / 库内 / 标签·形式社团页：键入关键字 → 服务端作用域作品搜索；清空 → 恢复原视图
    if (['libs', 'libview', 'makers'].includes(v)) {
      if (kw) scopedWorkSearch(st, kw, ++searchGen);
      else { searchGen++; render(); }
      return;
    }
    if (['works', 'favorites', 'filter', 'genreworks'].includes(v)) drawWorks(curItems);
    else if (lastMap) drawGroups(curItems, lastMap, lastUnit);
  }, 200);
});

// ---------- 启动 ----------
async function boot() {
  applyLayout();
  buildTabs();
  try { const s = await fetch('/api/state').then(r => r.json()); if (s.needLogin) { showLogin(true); return; } } catch (e) {}
  restoreFromHash();
}
// 按地址栏哈希恢复：叶子分区直接进入；卡片分区可深链接到具体层级（刷新/分享）
function restoreFromHash() {
  const p = parseHash();
  if (!p) { selectSection('medialib'); return; }
  if (p.key === 'searchdownload') { restoreSearchDownload(p); return; }
  if (!p.root) { selectSection(p.key); return; }
  section = p.key; stopTimers(); closeVideo(); closeAudio(); closeDrawer();
  buildTabs();
  makerSort = 0; workSort = 0;
  stack = p.stack.length ? p.stack : [{ view: SECTIONS.find(x => x.key === p.key).root, ctx: {} }];
  $('search').hidden = false; $('search').value = '';
  history.replaceState({ nav: true, depth: stack.length }, '', encodeHash());
  render();
}
// 恢复下载搜索分区：先按常规进入（基线为下载视图），再切到目标子视图；搜索带查询词则复跑一次
function restoreSearchDownload(p) {
  selectSection('searchdownload');
  if (p.sd === 'download') return;
  sdView = p.sd;
  renderSearchDownload();   // search 视图会同步建好 #sId
  if (p.sd === 'search' && p.query) { const inp = $('sId'); if (inp) inp.value = p.query; }
  history.replaceState({ nav: true, sd: p.sd }, '', encodeSdHash());
  if (p.sd === 'search' && p.query) runSearch();
}
boot();
