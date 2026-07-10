// core.js —— 框架层：DOM 工具 / 页内弹窗 / API / 登录 / 导航路由 / 卡片渲染基元。最先加载。
const $ = (id) => document.getElementById(id);
const el = (tag, cls, txt) => { const e = document.createElement(tag); if (cls) e.className = cls; if (txt != null) e.textContent = txt; if (tag === 'input') { e.autocomplete = 'off'; e.setAttribute('data-lpignore', 'true'); } return e; };
const enc = encodeURIComponent;

// 复制文本到剪贴板：优先 navigator.clipboard（需 https/localhost），否则退回 execCommand（局域网 http 下可用）
function copyText(t) {
  if (navigator.clipboard && navigator.clipboard.writeText) { navigator.clipboard.writeText(t).catch(() => fallbackCopy(t)); }
  else fallbackCopy(t);
}
function fallbackCopy(t) {
  const ta = document.createElement('textarea');
  ta.value = t; ta.style.position = 'fixed'; ta.style.opacity = '0';
  document.body.appendChild(ta); ta.select();
  try { document.execCommand('copy'); } catch (e) { }
  document.body.removeChild(ta);
}

// ---------- 页内弹窗（替代浏览器原生 alert/confirm/prompt，避免使用浏览器顶部弹窗）----------
// 返回 Promise：alert→resolve(undefined)；confirm→resolve(true/false)；prompt→resolve(字符串或 null)
let _modalCancel = null;   // 遮罩点击时用于取消当前弹窗的回调（保证 Promise 一定被 resolve，不悬挂）
function uiModal({ title, message, input, defaultValue, okText = '确定', cancelText, danger }) {
  return new Promise(resolve => {
    const ov = $('modal'), box = $('modalBox');
    box.innerHTML = '';
    const done = (v) => { _modalCancel = null; ov.classList.remove('show'); document.removeEventListener('keydown', onKey); resolve(v); };
    if (title) box.appendChild(el('h3', null, title));
    if (message != null && message !== '') box.appendChild(el('div', 'modal-msg', message));
    let field = null;
    if (input) {
      field = el('input', 'modal-input'); field.value = defaultValue || '';
      field.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); done(field.value); } });
      box.appendChild(field);
    }
    const btns = el('div', 'modal-btns');
    if (cancelText) { const c = el('button', 'icon-btn', cancelText); c.onclick = () => done(input ? null : false); btns.appendChild(c); }
    const ok = el('button', 'icon-btn primary' + (danger ? ' danger' : ''), okText);
    ok.onclick = () => done(input ? field.value : true);
    btns.appendChild(ok); box.appendChild(btns);
    // Esc 取消 / Enter 确认（有输入框时 Enter 由输入框处理）
    function onKey(e) {
      if (e.key === 'Escape') { e.preventDefault(); done(input ? null : false); }
      else if (e.key === 'Enter' && !input) { e.preventDefault(); done(true); }
    }
    _modalCancel = () => done(input ? null : false);
    document.addEventListener('keydown', onKey);
    ov.classList.add('show');
    (field || ok).focus();
  });
}
function uiAlert(message, title) { return uiModal({ title: title || '提示', message, okText: '确定' }); }
function uiConfirm(message, opts = {}) { return uiModal({ title: opts.title || '确认', message, okText: opts.okText || '确定', cancelText: opts.cancelText || '取消', danger: opts.danger }); }
function uiPrompt(message, defaultValue, title) { return uiModal({ title: title || '输入', message, input: true, defaultValue, cancelText: '取消' }); }
$('modal').addEventListener('click', e => { if (e.target.id === 'modal' && _modalCancel) _modalCancel(); });

const SECTIONS = [
  { key: 'medialib', label: '媒体库', root: 'libs',     icon: '📚' },
  { key: 'searchdownload', label: '下载搜索',            icon: '⬇' },
  { key: 'tag',      label: '作品标签', root: 'genres',   icon: '🏷' },
  { key: 'maker',    label: '作品社团', root: 'allmakers', icon: '🏢' },
  { key: 'type',     label: '作品形式', root: 'types',   icon: '🎬' },
  { key: 'favorite', label: '我的收藏', root: 'favorites', icon: '♥' },
  { key: 'settings', label: '系统设置',                     icon: '⚙' },
];
const MAKER_SORTS = ['作品数 多→少', '作品数 少→多', '社团名 A→Z'];
const WORK_SORTS  = ['发售日 新→旧', '发售日 旧→新', 'RJ号 新→旧', 'RJ号 旧→新'];
const AGE = { '1': '全年龄', '2': 'R-15', '3': 'R-18' };
const VIDEO_EXTS = ['.mp4', '.mkv', '.avi', '.wmv', '.mov', '.flv', '.webm', '.m4v', '.ts', '.mpg', '.mpeg'];
const AUDIO_EXTS = ['.mp3', '.wav', '.flac', '.m4a', '.aac', '.ogg', '.wma', '.opus'];
const IMG_EXTS = ['.jpg', '.jpeg', '.png', '.gif', '.webp'];
const fileUrl = (id, rel) => `/api/file?id=${enc(id)}&path=${enc(rel)}`;

let section = 'medialib';
let stack = [];            // 卡片区导航栈
let curItems = [];         // 卡片区原始数据（本地搜索过滤）
let makerSort = 0, workSort = 0;
let pollTimer = null, usageTimer = null;

async function api(path) {
  const r = await fetch(path, { headers: { 'Accept': 'application/json' } });
  if (r.status === 401) { showLogin(false); throw new Error('unauth'); }
  return r.json();
}
async function apiPost(path, body) {
  const r = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) });
  if (r.status === 401) { showLogin(false); throw new Error('unauth'); }
  return r.json();
}

// ---------- 登录 ----------
function showLogin(initial) { $('login').classList.add('show'); if (!initial) $('loginErr').textContent = '会话已失效，请重新登录'; $('pw').focus(); }
async function doLogin() {
  $('loginErr').textContent = '';
  const r = await apiPost('/api/login', { password: $('pw').value });
  if (r.ok) { $('login').classList.remove('show'); $('pw').value = ''; selectSection('medialib'); }
  else $('loginErr').textContent = '密码错误';
}
// Enter 提交由登录表单的 onsubmit 处理，无需额外监听（避免重复提交）

// ---------- 左侧导航（桌面常驻 / 移动抽屉） ----------
function buildTabs() {
  const box = $('nav'); box.innerHTML = '';
  box.appendChild(el('div', 'brand', 'DLsite媒体库'));
  SECTIONS.forEach(s => {
    const b = el('div', 'navitem' + (s.key === section ? ' active' : ''));
    b.appendChild(el('span', null, s.label));
    b.onclick = () => selectSection(s.key);
    box.appendChild(b);
  });
}
// 移动端抽屉开关
function toggleDrawer() {
  const open = $('nav').classList.toggle('open');
  $('drawerBackdrop').classList.toggle('show', open);
}
function closeDrawer() {
  $('nav').classList.remove('open');
  $('drawerBackdrop').classList.remove('show');
}
// 设备/窗口决定布局：iOS/Android 手机或窄窗 → 抽屉；Windows/macOS/iPadOS/平板 → 侧栏常驻
function applyLayout() {
  const ua = navigator.userAgent;
  const phone = /iPhone|iPod|Windows Phone/.test(ua) || (/Android/.test(ua) && /Mobile/.test(ua));
  const mobile = phone || window.innerWidth <= 700;
  document.body.classList.toggle('layout-mobile', mobile);
  document.body.classList.toggle('layout-desktop', !mobile);
  if (!mobile) closeDrawer();
}
window.addEventListener('resize', applyLayout);
function stopTimers() { if (pollTimer) clearInterval(pollTimer); if (usageTimer) clearInterval(usageTimer); pollTimer = usageTimer = null; }

function selectSection(key) {
  section = key; stopTimers(); closeVideo(); closeAudio(); closeDrawer();
  const s = SECTIONS.find(x => x.key === key);
  buildTabs();
  $('search').hidden = !s.root;
  $('sort').hidden = true;
  $('back').hidden = true;
  $('count').textContent = '';
  $('content').innerHTML = '';
  $('search').value = '';
  $('detailActions').hidden = true; $('detailActions').innerHTML = '';   // 清除上一分区遗留的详情操作按钮
  $('topbar').classList.remove('detail-mode');
  if (s.root) { makerSort = 0; workSort = 0; stack = [{ view: s.root, ctx: {} }]; }
  // 反映到地址栏；切换分区重置历史基线（不累积层级），但 URL 可见当前分区
  history.replaceState({ nav: true, depth: s.root ? 1 : 0 }, '', encodeHash());
  if (s.root) render();
  else if (key === 'searchdownload') { sdView = 'download'; renderSearchDownload(); }
  else if (key === 'settings') renderSettings();
}

// ---------- URL 路由（可见地址栏路由，非隐藏）----------
// 地址栏哈希反映当前分区 + 卡片区导航路径，可分享/刷新恢复。
// 内存 stack 仍是渲染与滚动恢复的主数据源；哈希仅作 URL 呈现与新开页面时的恢复。
function encodeHash() {
  const s = SECTIONS.find(x => x.key === section);
  if (!s || !s.root) return '#/' + section;             // 叶子分区（下载/搜索/设置）
  const segs = stack.slice(1).map(e => {
    const ctx = { ...e.ctx }; delete ctx.nodes;          // nodes 为运行时文件树缓存，过大不入 URL
    const c = Object.keys(ctx).length ? '~' + encodeURIComponent(JSON.stringify(ctx)) : '';
    return e.view + c;
  });
  return '#/' + s.key + (segs.length ? '/' + segs.join('/') : '');
}
function parseHash() {
  const raw = (location.hash || '').replace(/^#\/?/, '');
  if (!raw) return null;
  const parts = raw.split('/').filter(Boolean);
  const s = SECTIONS.find(x => x.key === parts[0]);
  if (!s) return null;
  const stk = s.root ? [{ view: s.root, ctx: {} }] : [];
  for (let i = 1; i < parts.length; i++) {
    const t = parts[i].indexOf('~');
    const view = t < 0 ? parts[i] : parts[i].slice(0, t);
    let ctx = {};
    if (t >= 0) { try { ctx = JSON.parse(decodeURIComponent(parts[i].slice(t + 1))); } catch (e) { } }
    stk.push({ view, ctx });
  }
  return { key: s.key, root: !!s.root, stack: stk };
}

// ========== 卡片区（媒体库/标签/作品形式/收藏）==========
// 每深入一级 push 一个历史项：iOS/安卓的"返回"手势触发 popstate 即可逐级回退，并恢复浏览位置
function pushView(view, ctx) {
  if (stack.length) stack[stack.length - 1].scroll = window.scrollY;
  stack.push({ view, ctx: ctx || {} });
  history.pushState({ nav: true, depth: stack.length }, '', encodeHash());
  render();
}
async function popView() {
  if (stack.length <= 1) return false;
  stack.pop();
  const top = stack[stack.length - 1];
  await render();
  window.scrollTo(0, top.scroll || 0);   // 返回上一级后恢复之前的浏览位置
  return true;
}
window.addEventListener('popstate', () => {
  const s = SECTIONS.find(x => x.key === section);
  if (s && s.root && stack.length > 1) popView();
});
$('back').onclick = () => history.back();   // 走 popstate，与手势行为一致

async function render() {
  const st = stack[stack.length - 1];
  $('back').hidden = stack.length <= 1;
  $('sort').hidden = true;
  $('libToggle').hidden = true;
  $('detailActions').hidden = true; $('detailActions').innerHTML = '';   // 顶栏操作按钮仅作品详情页使用
  $('topbar').classList.remove('detail-mode');   // 详情页专属排布，离开即复原
  $('search').hidden = (st.view === 'detail' || st.view === 'files');   // 作品详情/查看作品页不显示搜索框
  $('content').innerHTML = '';
  $('count').textContent = '';
  try {
    if (st.view === 'libs') await renderLibs();
    else if (st.view === 'libview') await renderLibView(st.ctx);
    else if (st.view === 'genres') await renderGenres();
    else if (st.view === 'types') await renderTypes();
    else if (st.view === 'allmakers') await renderAllMakers();
    else if (st.view === 'favorites') await renderWorks('/api/favorites', '我的收藏', true);
    else if (st.view === 'makers') await renderMakers(st.ctx);
    else if (st.view === 'genreworks') await renderWorks('/api/genreworks?genre=' + enc(st.ctx.genre), st.ctx.label || st.ctx.genre, true);
    else if (st.view === 'works') await renderWorks(worksUrl(st.ctx), st.ctx.maker || '未知社团', true, st.ctx);
    else if (st.view === 'filter') await renderWorks(`/api/filter?col=${enc(st.ctx.col)}&val=${enc(st.ctx.val)}`, st.ctx.label, true);
    else if (st.view === 'detail') await renderDetail(st.ctx.id);
    else if (st.view === 'files') await renderFiles(st.ctx);
  } catch (e) { if (e.message !== 'unauth') $('content').appendChild(el('div', 'empty', '加载失败')); }
}

function setSort(options, current, onChange) {
  const s = $('sort'); s.hidden = false; s.innerHTML = '';
  options.forEach((o, i) => { const op = el('option', null, o); op.value = i; s.appendChild(op); });
  s.value = current; s.onchange = () => onChange(parseInt(s.value));
}

function filtered() { const kw = $('search').value.trim().toLowerCase(); return kw ? curItems.filter(i => i._key.includes(kw)) : curItems; }

let lastMap = null, lastUnit = '';
function drawGroups(items, mapFn, unit) {
  lastMap = mapFn; lastUnit = unit;
  const items2 = filtered();
  const works = items.reduce((s, i) => s + (i.works || i.count || 0), 0);
  $('count').textContent = $('search').value.trim() ? `共 ${items.length} ${unit}，匹配 ${items2.length} 个` : `共 ${items.length} ${unit}，${works} 个作品`;
  const grid = el('div', 'grid groups');
  items2.forEach(i => { const m = mapFn(i); const c = el('div', 'card group-card'); c.appendChild(el('div', 'gt', m.title)); c.appendChild(el('div', 'gc', m.caption)); c.onclick = m.onClick; grid.appendChild(c); });
  const host = $('content'); host.innerHTML = ''; host.appendChild(items2.length ? grid : el('div', 'empty', '没有内容'));
}
function makeWorkCard(w) {
  const c = el('div', 'card');
  const cov = el('div', 'cover');
  if (w.cover) { const img = el('img'); img.loading = 'lazy'; img.src = `/api/cover?id=${enc(w.id)}`; cov.appendChild(img); }
  cov.appendChild(el('div', 'badge rj', w.id));
  if (w.type) cov.appendChild(el('div', 'badge type', w.type));
  c.appendChild(cov); c.appendChild(el('div', 'wt', w.name || w.id));
  c.onclick = () => pushView('detail', { id: w.id });
  return c;
}
// 分批懒加载：一次只建 CARD_BATCH 张卡片，滚动接近底部时再补下一批。
// 媒体库上千个作品、或单个作品内上百张图片时，避免一次性构建 DOM + 发起大量图片请求。
const CARD_BATCH = 30;
let _cardIO = null;   // 全局仅一个 observer（同一时刻只有一个懒加载网格在展示）
// 把 items 分批用 makeFn 渲染进「已在 DOM 中」的 grid；哨兵滚入视口（提前 600px）时补下一批。
// unobserve→observe 触发一次新的相交回调，处理"补完后哨兵仍在视口"的连续加载。
function lazyGridFill(grid, items, makeFn) {
  if (_cardIO) { _cardIO.disconnect(); _cardIO = null; }
  let idx = 0;
  const renderNext = () => {
    const end = Math.min(idx + CARD_BATCH, items.length);
    const frag = document.createDocumentFragment();
    for (; idx < end; idx++) frag.appendChild(makeFn(items[idx]));
    grid.appendChild(frag);
  };
  renderNext();
  if (idx >= items.length) return;
  const sentinel = el('div'); sentinel.style.height = '1px'; grid.after(sentinel);
  // rootMargin 为 0：哨兵（网格末尾）真正滚入视口——即滑块拉到底——时才加载下一批，
  // 不做提前预取。unobserve→observe 触发新回调，处理"补完后仍在底部"的连续加载。
  _cardIO = new IntersectionObserver(es => {
    if (!es[0].isIntersecting) return;
    _cardIO.unobserve(sentinel);
    renderNext();
    if (idx < items.length) _cardIO.observe(sentinel);
    else { _cardIO.disconnect(); _cardIO = null; sentinel.remove(); }
  }, { rootMargin: '0px' });
  _cardIO.observe(sentinel);
}
function lazyCards(host, items, makeFn, emptyText) {
  if (_cardIO) { _cardIO.disconnect(); _cardIO = null; }
  host.innerHTML = '';
  if (!items.length) { host.appendChild(el('div', 'empty', emptyText)); return; }
  const grid = el('div', 'grid cards'); host.appendChild(grid);
  lazyGridFill(grid, items, makeFn);
}
function drawWorks(items) {
  lastMap = null;
  const items2 = filtered();
  $('count').textContent = $('search').value.trim() ? `共 ${items.length} 个作品，匹配 ${items2.length} 个` : `共 ${items.length} 个作品`;
  lazyCards($('content'), items2, makeWorkCard, '没有内容');
}
