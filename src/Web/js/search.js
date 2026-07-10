// search.js —— 下载搜索·搜索子视图：作品号/社团号搜索、AS 论坛扫描、加入下载队列。
// ========== 搜索 ==========
function renderSearch() {
  $('title').textContent = '';
  // 重置社团状态：让可能残留的后台扫描循环（makerState !== s）自行停止
  if (makerState && makerState.io) makerState.io.disconnect();
  makerState = null;
  const host = $('content'); host.innerHTML = '';
  const bar = el('div', 'toolbar');
  const back = el('button', 'icon-btn', '← 下载'); back.onclick = () => { sdView = 'download'; renderSearchDownload(); };
  const inp = el('input'); inp.placeholder = '作品号(RJ/BJ/VJ)、社团号(RG) 或 DLsite 链接'; inp.className = 'grow'; inp.id = 'sId';
  const btn = el('button', 'icon-btn primary', '查询');
  inp.addEventListener('keydown', e => { if (e.key === 'Enter') runSearch(); });
  btn.onclick = runSearch;
  bar.append(back, inp, btn);
  host.appendChild(bar);
  // 社团网格与帖子列表分两个 pane：进入帖子列表时仅隐藏社团 pane（保留 DOM 与后台扫描），返回即缓存恢复
  const wrap = el('div'); wrap.id = 'searchResult';
  const mp = el('div'); mp.id = 'makerPane';
  const wp = el('div'); wp.id = 'workPane';
  wrap.append(mp, wp); host.appendChild(wrap);
}
function showMakerPane() { $('makerPane').style.display = ''; $('workPane').style.display = 'none'; }
function showWorkPane() { $('makerPane').style.display = 'none'; $('workPane').style.display = ''; }
function goBackToMaker() {
  showMakerPane();
  if (makerState) {
    $('count').textContent = makerState.countText || '';
    // 恢复到点击进入作品前的浏览位置（社团网格 DOM 一直保留，仅切换过 display）
    requestAnimationFrame(() => window.scrollTo(0, makerState.scrollY || 0));
  }
}
// 识别输入：作品号 / 社团号 / DLsite 链接
function parseInput(raw) {
  raw = (raw || '').trim();
  let m = raw.match(/product_id\/((?:RJ|BJ|VJ)\d+)/i); if (m) return { kind: 'work', id: m[1].toUpperCase() };
  m = raw.match(/maker_id\/(RG\d+)/i); if (m) return { kind: 'maker', id: m[1].toUpperCase() };
  const up = raw.toUpperCase();
  if (/^(?:RJ|BJ|VJ)\d+$/.test(up)) return { kind: 'work', id: up };
  if (/^RG\d+$/.test(up)) return { kind: 'maker', id: up };
  return { kind: 'invalid', id: '' };
}
async function runSearch() {
  const p = parseInput($('sId').value);
  if (p.kind === 'maker') { await runMakerSearch(p.id); return; }
  if (p.kind !== 'work') { showWorkPane(); $('workPane').innerHTML = '<div class="empty">请输入作品号(RJ/BJ/VJ)、社团号(RG) 或 DLsite 链接</div>'; return; }
  makerReturnId = null;
  $('sId').value = p.id;
  await runWorkSearch(p.id);
}
let workGen = 0;
async function runWorkSearch(id) {
  const gen = ++workGen;
  showWorkPane();
  const box = $('workPane'); box.innerHTML = '<div class="empty"><span class="spin"></span> 正在查询…</div>';
  let d;
  try { d = await api('/api/search?id=' + enc(id)); } catch (e) { return; }
  if (gen !== workGen) return;
  if (d.error) { box.innerHTML = ''; box.appendChild(el('div', 'empty', d.error)); return; }
  if (d.existed && !await uiConfirm(`${d.id} ${d.existedName || ''}\n该作品已于 ${d.existedTime || ''} 加入过下载，是否继续？`)) {
    if (makerReturnId) goBackToMaker(); else box.innerHTML = '';
    return;
  }
  if (!d.results.length) { box.innerHTML = ''; box.appendChild(el('div', 'empty', '无匹配数据')); return; }
  box.innerHTML = '';
  if (makerReturnId) { const bk = el('button', 'icon-btn', '← 返回社团作品'); bk.style.marginBottom = '10px'; bk.onclick = goBackToMaker; box.appendChild(bk); }
  $('count').textContent = `${d.work.name || d.id} · ${d.results.length} 个帖子`;
  const posts = [];
  d.results.forEach(r => {
    const c = el('div', 'post');
    const head = el('div', 'phead');
    if (r.thumb) { const img = el('img'); img.loading = 'lazy'; img.src = '/api/thumb?url=' + enc(r.thumb); head.appendChild(img); }
    const info = el('div', 'pinfo');
    info.appendChild(el('div', 'rt', r.title));
    if (r.minor) info.appendChild(el('div', 'rm', r.minor));
    if (r.snippet) info.appendChild(el('div', 'rs', r.snippet));
    head.appendChild(info);
    const statusEl = el('div', 'rstatus', '待扫描'); statusEl.style.color = 'var(--muted)'; head.appendChild(statusEl);
    const hostsEl = el('div', 'rhosts');
    c.append(head, hostsEl);
    const post = { url: r.url, statusEl, hostsEl, scanned: false, scanning: false };
    // 点击未扫描的帖子可手动补扫（自动扫描命中并停止后，仍可点击后续帖子）
    head.onclick = () => { if (!post.scanning && !post.scanned) scanPost(id, post, workGen); };
    box.appendChild(c);
    posts.push(post);
  });
  autoScanPosts(id, gen, posts);
}
// 从上到下自动扫描帖子，命中含有效下载的帖子即停止
async function autoScanPosts(id, gen, posts) {
  for (const post of posts) {
    if (gen !== workGen) return;
    if (post.scanned || post.scanning) continue;
    const valid = await scanPost(id, post, gen);
    if (gen !== workGen) return;
    if (valid) return;
  }
}
// 扫描单个帖子：抓网盘链接 → 内联展示 → 检测；存在有效网盘则返回 true 并可直接下载
async function scanPost(id, post, gen) {
  post.scanning = true;
  post.statusEl.textContent = '扫描中…'; post.statusEl.style.color = 'var(--muted)';
  let d;
  try { d = await api('/api/posturls?url=' + enc(post.url)); } catch (e) { post.scanning = false; return false; }
  if (gen !== workGen) { post.scanning = false; return false; }
  post.hostsEl.innerHTML = '';
  if (d.error || !d.hosts || !d.hosts.length) {
    post.statusEl.textContent = '无下载链接'; post.statusEl.style.color = 'var(--muted)';
    post.scanned = true; post.scanning = false; return false;
  }
  post.statusEl.textContent = '检测中…';
  const checks = d.hosts.map(h => {
    const c = el('div', 'host');
    c.appendChild(el('div')).append(el('div', 'hn', h.host), el('div', 'hc', `${h.count} 个文件`));
    const st = el('div', 'hs'); st.innerHTML = '<span class="spin"></span> 检测中…'; c.appendChild(st);
    post.hostsEl.appendChild(c);
    return apiPost('/api/checkhost', { urls: h.urls }).then(res => {
      if (gen !== workGen) return false;
      if (res.status === 'valid') { st.textContent = '点击下载'; st.style.color = '#4ade80'; c.classList.add('ok'); c.onclick = (ev) => { ev.stopPropagation(); enqueue(id, h.urls, c, st); }; return true; }
      if (res.status === 'invalid') { st.textContent = '失效'; st.style.color = '#f87171'; return false; }
      st.textContent = `部分有效 ${res.valid}/${res.total}`; st.style.color = '#facc15'; return false;
    }).catch(() => { st.textContent = '检测失败'; st.style.color = '#f87171'; return false; });
  });
  const results = await Promise.all(checks);
  if (gen !== workGen) { post.scanning = false; return false; }
  const anyValid = results.some(Boolean);
  post.statusEl.textContent = anyValid ? '有有效下载' : '无有效下载';
  post.statusEl.style.color = anyValid ? '#4ade80' : '#facc15';
  post.scanned = true; post.scanning = false;
  return anyValid;
}

// 社团（RG）搜索：作品缩略图网格 + 下拉到底自动加载下一页
let makerState = null, makerReturnId = null;
async function runMakerSearch(makerId) {
  showMakerPane();
  $('count').textContent = '';
  const box = $('makerPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在获取社团作品…</div>';
  if (makerState && makerState.io) makerState.io.disconnect();
  makerState = { id: makerId, page: 0, hasMore: true, loading: false, scanQueue: [], scanning: false, cards: {}, countText: '' };
  box.innerHTML = '';
  const grid = el('div', 'grid cards'); box.appendChild(grid); makerState.grid = grid;
  const sentinel = el('div'); sentinel.style.height = '1px'; box.appendChild(sentinel);
  await loadMakerPage();
  const io = new IntersectionObserver(es => { if (es[0].isIntersecting) loadMakerPage(); });
  io.observe(sentinel); makerState.io = io;
}
async function loadMakerPage() {
  const s = makerState; if (!s || s.loading || !s.hasMore) return;
  s.loading = true;
  let d;
  try { d = await api(`/api/maker?id=${enc(s.id)}&page=${s.page + 1}`); } catch (e) { s.loading = false; return; }
  if (d.error || !d.works || !d.works.length) {
    if (s.page === 0) { const mp = $('makerPane'); mp.innerHTML = ''; mp.appendChild(el('div', 'empty', d.error || '未找到该社团的作品')); }
    s.hasMore = false; s.loading = false; if (s.io) s.io.disconnect(); return;
  }
  s.page++; s.hasMore = d.hasMore;
  let hasDownloading = false;
  d.works.forEach(w => {
    // 下载中 → 下载状态角标（同步下载页）；已下载/已品悦 → 置灰在库角标；无记录 → 排入 AS 扫描
    const inLib = w.state === '已品悦' || w.state === '已下载';
    const downloading = w.state === '下载中';
    const c = el('div', 'card mk' + (inLib ? ' dim' : ''));
    const cov = el('div', 'cover');
    if (w.thumb) { const img = el('img'); img.loading = 'lazy'; img.src = '/api/thumb?url=' + enc(w.thumb); cov.appendChild(img); }
    cov.appendChild(el('div', 'badge rj', w.id));
    let badge;
    if (inLib) { badge = el('div', 'badge lib', w.state); }
    else if (downloading) { badge = el('div', 'badge as', '下载中'); badge.style.color = '#60a5fa'; badge.dataset.dl = '1'; hasDownloading = true; }
    else { badge = el('div', 'badge as', '待扫描'); s.scanQueue.push(w.id); }
    cov.appendChild(badge);
    s.cards[w.id] = badge;
    c.appendChild(cov);
    // 底部区（占卡片高度 1/3）：标题 + 卡内常驻操作按钮（默认禁用/屏蔽，AS 命中帖子后由 runMakerScans 启用）
    const foot = el('div', 'mk-foot');
    foot.appendChild(el('div', 'wt', w.title || w.id));
    const acts = el('div', 'mcard-actions');
    const goList = () => { makerReturnId = s.id; s.scrollY = window.scrollY; $('sId').value = w.id; runWorkSearch(w.id); };
    const listBtn = el('button', null, '结果列表'); listBtn.disabled = true;
    listBtn.onclick = (ev) => { ev.stopPropagation(); goList(); };
    const autoBtn = el('button', 'primary', '自动下载'); autoBtn.disabled = true;
    autoBtn.onclick = (ev) => { ev.stopPropagation(); autoDownload(w.id, s.cards[w.id], autoBtn); };
    acts.append(listBtn, autoBtn);
    foot.appendChild(acts);
    c.appendChild(foot);
    // 封面/标题区仍可点击进入帖子列表（按钮已 stopPropagation），保留在库/下载中卡片的查看入口
    c.onclick = goList;
    s.grid.appendChild(c);
  });
  s.countText = `${s.grid.children.length} 个作品`;
  $('count').textContent = s.countText;
  s.loading = false;
  if (!s.hasMore && s.io) s.io.disconnect();
  runMakerScans();
  if (hasDownloading) startMakerDownPoll();
}
// 把下载列表聚合状态写回社团卡片角标（每秒，镜像 /api/downloads 的分组状态），无活动下载即止
async function startMakerDownPoll() {
  const s = makerState;
  if (!s || s.dlPolling) return;
  s.dlPolling = true;
  while (true) {
    if (makerState !== s || section !== 'searchdownload' || sdView !== 'search') break;
    let d;
    try { d = await api('/api/downloads'); } catch (e) { break; }
    if (makerState !== s) break;
    const map = {}; (d.groups || []).forEach(g => { map[g.id] = g; });
    Object.keys(s.cards).forEach(id => {
      const g = map[id]; if (!g) return;
      const b = s.cards[id];
      b.dataset.dl = '1';
      b.textContent = g.statusText;
      b.style.background = 'rgba(0,0,0,.72)';
      b.style.color = g.color;
      const card = b.closest('.card'); if (card) card.classList.remove('dim');
    });
    if (!(d.groups || []).length) break;   // 无活动下载则停止轮询
    await new Promise(r => setTimeout(r, 1000));
  }
  s.dlPolling = false;
}
// 对未在库作品按 3 秒间隔逐个扫描 AS 论坛，把结果写回卡片角标
async function runMakerScans() {
  const s = makerState;
  if (!s || s.scanning) return;
  s.scanning = true;
  while (s.scanQueue.length) {
    if (makerState !== s || section !== 'searchdownload' || sdView !== 'search') break;   // 已开始新搜索或离开搜索页则停止
    const id = s.scanQueue.shift();
    const st = s.cards[id];
    if (!st) continue;
    st.innerHTML = '<span class="spin"></span>';
    let d;
    try { d = await api('/api/asscan?id=' + enc(id)); } catch (e) { break; }
    if (makerState !== s || section !== 'searchdownload' || sdView !== 'search') break;
    if (d.count > 0) {
      st.textContent = `AS · ${d.count} 帖`; st.style.background = 'rgba(34,160,80,.88)';
      const card = st.closest('.card'); if (card) card.querySelectorAll('.mcard-actions button').forEach(b => b.disabled = false);   // 有帖子 → 解除按钮屏蔽
    }
    else if (d.count === 0) { st.textContent = 'AS · 无'; st.style.background = 'rgba(0,0,0,.62)'; }
    else { st.textContent = 'AS · 失败'; st.style.background = 'rgba(180,60,60,.88)'; }
    if (s.scanQueue.length) await new Promise(r => setTimeout(r, 3000));   // 仅在还有待扫描项时间隔等待
  }
  s.scanning = false;
}
// 自动下载：选好下载目标库后，立即把作品以「搜索可用下载连接」状态加入下载队列；服务端后台
// 逐帖检测并挑选最优源——命中则转正常下载，全无则队列状态置「无可用下载连接」。免去用户进结果列表手动挑源。
async function autoDownload(id, badge, btn) {
  if (btn.disabled) return;
  const t = await api('/api/downtargets');
  let lib = '', folder = '';
  if (t.libs && t.libs.length) {
    const target = await pickTarget(t.libs);
    if (!target) return;   // 用户取消选库
    lib = target.lib; folder = target.folder;
  }
  btn.disabled = true; const orig = btn.textContent; btn.textContent = '入队中…';
  const r = await apiPost('/api/autodownload', { id, lib, folder });
  if (r.ok) {
    btn.textContent = '已入队';   // 保持禁用，避免重复入队
    // 角标即刻转「搜索连接中」，随后由 startMakerDownPoll 按 /api/downloads 聚合状态刷新
    if (badge) { badge.dataset.dl = '1'; badge.textContent = '搜索连接中'; badge.style.background = 'rgba(0,0,0,.72)'; badge.style.color = '#facc15'; }
    const mc = badge && badge.closest('.card'); if (mc) mc.classList.remove('dim');
    startMakerDownPoll();
  } else { btn.disabled = false; btn.textContent = orig; await uiAlert('自动下载失败：' + (r.error || r.message || '')); }
}
async function enqueue(id, urls, card, st) {
  const d = await api('/api/downtargets');
  let lib = '', folder = '';
  if (d.libs.length) {
    const target = await pickTarget(d.libs);
    if (!target) return;
    lib = target.lib; folder = target.folder;
  }
  st.innerHTML = '<span class="spin"></span>';
  const r = await apiPost('/api/enqueue', { id, urls, lib, folder });
  if (r.ok) {
    st.textContent = '已加入下载'; st.style.color = '#6aa3ff'; card.classList.remove('ok'); card.onclick = null;
    // 同步社团卡片：该作品立即转为"待下载"，并启动与下载页的状态同步
    if (makerState && makerState.cards[id]) {
      const b = makerState.cards[id];
      b.dataset.dl = '1'; b.textContent = '待下载'; b.style.background = 'rgba(0,0,0,.72)'; b.style.color = '#facc15';
      const mc = b.closest('.card'); if (mc) mc.classList.remove('dim');
      startMakerDownPoll();
    }
  } else { st.textContent = '加入失败'; st.style.color = '#f87171'; }
}
function pickTarget(libs) {
  return new Promise(resolve => {
    const ov = $('picker'), box = $('pickerBox');
    const close = (v) => { ov.classList.remove('show'); resolve(v); };
    function libPage() {
      box.innerHTML = ''; box.appendChild(el('h3', null, '选择下载到哪个媒体库'));
      libs.forEach(l => {
        const b = el('button', 'icon-btn', l.folders.length > 1 ? `${l.name}（${l.folders.length} 个文件夹）` : l.name);
        b.style.cssText = 'display:block;width:100%;text-align:left;margin-top:8px';
        b.onclick = () => { if (l.folders.length === 1) close({ lib: l.name, folder: l.folders[0] }); else folderPage(l); };
        box.appendChild(b);
      });
      const c = el('button', 'icon-btn', '取消'); c.style.cssText = 'display:block;width:100%;margin-top:16px'; c.onclick = () => close(null); box.appendChild(c);
    }
    function folderPage(l) {
      box.innerHTML = ''; box.appendChild(el('h3', null, '选择下载文件夹'));
      l.folders.forEach(f => { const b = el('button', 'icon-btn', f); b.style.cssText = 'display:block;width:100%;text-align:left;margin-top:8px;word-break:break-all'; b.onclick = () => close({ lib: l.name, folder: f }); box.appendChild(b); });
      const bk = el('button', 'icon-btn', '← 返回'); bk.style.cssText = 'display:block;width:100%;margin-top:16px'; bk.onclick = libPage; box.appendChild(bk);
    }
    libPage(); ov.classList.add('show');
  });
}
