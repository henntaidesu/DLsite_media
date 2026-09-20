// fanbox.js ——「作品搜索」分区里的 FANBOX 数据源（pawchive）：搜作家 → 作家主页选作品 → 批量/单篇下载。
// 只做搜索与下载，不含媒体库浏览：已下载的 fanbox 作品落在磁盘的媒体库目录里，程序内不再单独建卡片视图。
// 工具栏（来源下拉 + 输入框 + 查询）由 search.js 统一提供，本模块只负责两个结果面板：
//   fbArtistPane 作家搜索结果（只有一个结果时直接进主页）
//   fbPostPane   作家主页：作品网格 + 卡内按钮选择 + 批量下载
let fbArtist = null;        // 当前作家 { id, name, publicId }
let fbPosts = [];           // 作家主页已加载的作品（跨分页累积）
let fbSel = new Set();      // 选中待下载的作品号
let fbPaging = null;        // { offset, hasMore, loading, io }
let fbGen = 0;              // 请求代际：新一轮搜索/换作家即作废在途请求
let fbFromArtists = false;  // 当前主页是否从作家列表点进来（决定「返回作家列表」是否显示）

const fbImg = (url) => '/api/fanbox/image?url=' + enc(url);

// ---------- 面板骨架 ----------

// 由 search.js 搭建搜索区时调用一次：建出作家/作品两个面板（与 DLsite 的 makerPane/workPane 同构）
function fbBuildPanes(host) {
  fbResetState();
  const wrap = el('div'); wrap.id = 'fbResult';
  const ap = el('div'); ap.id = 'fbArtistPane';
  const pp = el('div'); pp.id = 'fbPostPane'; pp.style.display = 'none';
  wrap.append(ap, pp);
  host.appendChild(wrap);
}

function fbResetState() {
  if (fbPaging && fbPaging.io) fbPaging.io.disconnect();
  fbPaging = null;
  fbArtist = null; fbPosts = []; fbSel = new Set(); fbFromArtists = false;
  fbGen++;
}

function fbShowArtistPane() {
  $('fbArtistPane').style.display = '';
  $('fbPostPane').style.display = 'none';
  fbUpdateBackBtn();
}

function fbShowPostPane() {
  $('fbArtistPane').style.display = 'none';
  $('fbPostPane').style.display = '';
  fbUpdateBackBtn();
}

// 工具栏「返回作家列表」：仅从作家搜索结果点进主页时显示（位置/逻辑同 DLsite 的「返回作品列表」）
function fbUpdateBackBtn() {
  const b = $('fbBackBtn');
  if (!b) return;
  const pane = $('fbPostPane');
  const onPosts = pane && pane.style.display !== 'none';
  b.style.display = (onPosts && fbFromArtists) ? '' : 'none';
}

function fbGoBackToArtists() {
  fbShowArtistPane();
  $('count').textContent = '';
}

// ---------- 搜索作家 ----------

// 输入可以是作家名、纯数字作家号，或直接粘 pawchive 的作家/作品链接
function fbParseInput(raw) {
  raw = (raw || '').trim();
  const m = raw.match(/\/fanbox\/user\/(\d+)/i);
  if (m) return { kind: 'artist', id: m[1] };
  if (/^\d{4,}$/.test(raw)) return { kind: 'artist', id: raw };
  return { kind: 'keyword', kw: raw };
}

// 由 search.js 的「查询」按钮 / 回车触发
async function fbRunSearch() {
  const raw = ($('sId') && $('sId').value.trim()) || '';
  if (!raw) return;
  const p = fbParseInput(raw);
  // 直接给出作家号（或粘链接）时跳过搜索，径直进主页
  if (p.kind === 'artist') { await fbOpenArtist({ id: p.id, name: '', publicId: '' }, false); return; }
  await fbSearchArtists(p.kw);
}

async function fbSearchArtists(kw) {
  const gen = ++fbGen;
  fbShowArtistPane();
  const box = $('fbArtistPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在搜索作家…（首次搜索需下载站点作家索引，约 15MB）</div>';
  $('count').textContent = '';
  let d;
  try { d = await api('/api/fanbox/artists?q=' + enc(kw)); } catch (e) { return; }
  if (gen !== fbGen) return;
  const artists = d.artists || [];
  // 只有一个结果 → 直接进入该作家主页（免去多余一次点击）
  if (artists.length === 1) { await fbOpenArtist(artists[0], false); return; }
  box.innerHTML = '';
  if (!artists.length) { box.appendChild(el('div', 'empty', '没有匹配的作家')); return; }
  $('count').textContent = `搜索到 ${artists.length} 位作家` + (d.cachedAt ? `　索引更新于 ${d.cachedAt}` : '');
  const grid = el('div', 'grid cards');
  artists.forEach(a => grid.appendChild(fbMakeArtistCard(a)));
  box.appendChild(grid);
}

function fbMakeArtistCard(a) {
  const c = el('div', 'card');
  const cov = el('div', 'cover');
  if (a.icon) { const img = el('img'); img.loading = 'lazy'; img.src = fbImg(a.icon); cov.appendChild(img); }
  if (a.publicId) cov.appendChild(el('div', 'badge rj', a.publicId));
  if (a.favorited) cov.appendChild(el('div', 'badge type', '♥ ' + a.favorited));
  c.append(cov, el('div', 'wt', a.name || a.publicId || a.id));
  c.onclick = () => fbOpenArtist(a, true);
  return c;
}

// ---------- 作家主页 ----------

async function fbOpenArtist(artist, fromArtists) {
  const gen = ++fbGen;
  fbArtist = { id: artist.id, name: artist.name || '', publicId: artist.publicId || '' };
  fbFromArtists = !!fromArtists;
  fbPosts = []; fbSel = new Set();
  if (fbPaging && fbPaging.io) fbPaging.io.disconnect();
  fbPaging = { offset: 0, hasMore: true, loading: false, io: null };

  fbShowPostPane();
  const box = $('fbPostPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在获取作品…</div>';

  const first = await fbFetchPosts(gen, 0);
  if (gen !== fbGen || !first) return;
  box.innerHTML = '';

  // 选择操作条：已选计数 + 全选 / 清空 / 下载选中
  const selbar = el('div', 'toolbar fb-selbar');
  const info = el('div', 'fb-selinfo'); info.id = 'fbSelInfo';
  const all = el('button', 'icon-btn', '全选'); all.onclick = () => fbSelectAll(true);
  const none = el('button', 'icon-btn', '清空'); none.onclick = () => fbSelectAll(false);
  const dl = el('button', 'icon-btn primary', '下载选中'); dl.id = 'fbDlBtn'; dl.onclick = fbDownloadSelected;
  selbar.append(info, all, none, dl);
  box.appendChild(selbar);

  const grid = el('div', 'grid cards'); grid.id = 'fbGrid'; box.appendChild(grid);
  fbAppendPosts(grid, first);
  fbSetupPaging(gen, grid, box);
  fbUpdateSelInfo();
}

async function fbFetchPosts(gen, offset) {
  const q = new URLSearchParams();
  q.set('id', fbArtist ? fbArtist.id : ''); q.set('service', 'fanbox'); q.set('o', offset);
  let d;
  try { d = await api('/api/fanbox/posts?' + q); } catch (e) { return null; }
  if (gen !== fbGen) return null;
  if (d.error) {
    const pane = $('fbPostPane');
    pane.innerHTML = ''; pane.appendChild(el('div', 'empty', d.error));
    return null;
  }
  if (d.artist) fbArtist = { id: d.artist.id, name: d.artist.name, publicId: d.artist.publicId };
  if (fbPaging) { fbPaging.offset = offset + (d.posts || []).length; fbPaging.hasMore = !!d.hasMore; }
  fbPosts = fbPosts.concat(d.posts || []);
  const who = (fbArtist && (fbArtist.name || fbArtist.id)) || '';
  $('count').textContent = `${who}　已加载 ${fbPosts.length} 篇作品` + (d.hasMore ? '（下拉加载更多）' : '');
  return d.posts || [];
}

// 下拉到底自动加载下一页（与 DLsite 社团作品网格同一交互）
function fbSetupPaging(gen, grid, box) {
  if (!fbPaging || !fbPaging.hasMore) return;
  const sentinel = el('div'); sentinel.style.height = '1px'; box.appendChild(sentinel);
  fbPaging.io = new IntersectionObserver(async es => {
    if (!es[0].isIntersecting || fbPaging.loading || !fbPaging.hasMore) return;
    fbPaging.loading = true;
    const more = await fbFetchPosts(gen, fbPaging.offset);
    if (gen !== fbGen) return;
    if (more && more.length) fbAppendPosts(grid, more);
    fbPaging.loading = false;
    if (!fbPaging.hasMore) { fbPaging.io.disconnect(); sentinel.remove(); }
  }, { rootMargin: '0px' });
  fbPaging.io.observe(sentinel);
}

function fbAppendPosts(grid, posts) {
  const frag = document.createDocumentFragment();
  posts.forEach(p => frag.appendChild(fbMakePostCard(p)));
  grid.appendChild(frag);
}

// 作品卡：结构与操作方式对齐 DLsite 作品卡（.card.mk）——封面占 2/3，
// 底部区放标题 + 卡内常驻操作按钮行（.mcard-actions）。
// 未下载：[选择/已选] [下载]；已入库/下载中：禁用的状态按钮。
function fbMakePostCard(p) {
  const done = p.state === '已品悦';
  const busy = p.state === '下载中' || p.state === '已下载';
  const picked = fbSel.has(p.id);
  const c = el('div', 'card mk' + (done || busy ? ' dim' : '') + (picked ? ' picked' : ''));

  const cov = el('div', 'cover');
  if (p.cover) { const img = el('img'); img.loading = 'lazy'; img.src = fbImg(p.cover); cov.appendChild(img); }
  if (p.files) cov.appendChild(el('div', 'badge type', p.files + ' 文件'));
  if (done || busy) cov.appendChild(el('div', 'badge lib', done ? '已下载' : p.state));
  c.appendChild(cov);

  const foot = el('div', 'mk-foot');
  foot.appendChild(el('div', 'wt', p.title || p.id));
  const acts = el('div', 'mcard-actions');
  if (done || busy) {
    const st = el('button', null, done ? '已下载' : p.state); st.disabled = true;
    acts.appendChild(st);
  } else {
    const selBtn = el('button', picked ? 'primary' : null, picked ? '已选' : '选择');
    selBtn.onclick = (ev) => { ev.stopPropagation(); fbTogglePick(p.id, c, selBtn); };
    const dlBtn = el('button', 'primary', '下载');
    dlBtn.title = '只下载这一篇';
    dlBtn.onclick = (ev) => { ev.stopPropagation(); fbDownloadOne(p.id, dlBtn); };
    acts.append(selBtn, dlBtn);
  }
  foot.appendChild(acts);
  c.appendChild(foot);

  // 封面/标题区点击等同于点「选择」（按钮已 stopPropagation）；已下载/下载中的卡片不可选
  c.onclick = () => { if (!done && !busy) fbTogglePick(p.id, c, acts.firstChild); };
  return c;
}

// 翻转单张卡片的选中态：整卡描边强调 + 按钮在 选择/已选 之间切换
function fbTogglePick(id, card, btn) {
  const on = !fbSel.has(id);
  if (on) fbSel.add(id); else fbSel.delete(id);
  if (card) card.classList.toggle('picked', on);
  if (btn) { btn.textContent = on ? '已选' : '选择'; btn.classList.toggle('primary', on); }
  fbUpdateSelInfo();
}

const fbPostSelectable = (p) => p.state !== '已品悦' && p.state !== '下载中' && p.state !== '已下载';

function fbSelectAll(on) {
  fbSel = new Set();
  if (on) fbPosts.forEach(p => { if (fbPostSelectable(p)) fbSel.add(p.id); });
  fbRedrawGrid();
  fbUpdateSelInfo();
}

// 按当前 fbPosts / fbSel 重建卡片：卡内按钮文案与选中描边随之刷新
function fbRedrawGrid() {
  const grid = $('fbGrid');
  if (!grid) return;
  grid.innerHTML = '';
  fbAppendPosts(grid, fbPosts);
}

function fbUpdateSelInfo() {
  const info = $('fbSelInfo'); if (info) info.textContent = `已选 ${fbSel.size} 篇`;
  const btn = $('fbDlBtn'); if (btn) btn.disabled = fbSel.size === 0;
}

// ---------- 下载 ----------

// 操作条「下载选中」：批量入队当前选中的作品
function fbDownloadSelected() {
  const btn = $('fbDlBtn');
  if (!btn || btn.disabled || !fbSel.size) return;
  return fbEnqueue([...fbSel], btn);
}

// 卡内「下载」：只入队这一篇
function fbDownloadOne(id, btn) {
  return fbEnqueue([id], btn);
}

// 选媒体库 → 入队 → 提示 → 就地把入队成功的作品转为「下载中」并取消其选中
async function fbEnqueue(ids, btn) {
  if (!ids.length || !fbArtist || (btn && btn.disabled)) return;
  let lib = '', folder = '';
  const t = await api('/api/downtargets');
  if (t.libs && t.libs.length) {
    const target = await pickTarget(t.libs, fbArtist.name || fbArtist.id);
    if (!target) return;
    lib = target.lib; folder = target.folder;
  }
  const orig = btn ? btn.textContent : '';
  if (btn) { btn.disabled = true; btn.textContent = '入队中…'; }
  let r;
  try {
    r = await apiPost('/api/fanbox/enqueue', {
      id: fbArtist.id, service: 'fanbox', posts: ids, lib, folder,
    });
  } catch (e) {
    if (btn) { btn.textContent = orig; btn.disabled = false; }
    return;
  }
  if (btn) { btn.textContent = orig; btn.disabled = false; }
  if (!r.ok) { await uiAlert(r.error || '加入下载失败'); return; }
  await uiAlert(`已加入下载：${r.posts} 篇作品 / ${r.files} 个文件` + (r.skipped ? `，跳过 ${r.skipped} 篇` : ''));
  ids.forEach(id => {
    const p = fbPosts.find(x => x.id === id);
    if (p) p.state = '下载中';
    fbSel.delete(id);
  });
  fbRedrawGrid();
  fbUpdateSelInfo();
}
