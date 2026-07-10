// settings.js —— 系统设置页 + 媒体库管理。
// ========== 设置 ==========
async function renderSettings() {
  $('title').textContent = '系统设置';
  const host = $('content'); host.innerHTML = '<div class="empty"><span class="spin"></span> 加载中…</div>';
  let d; try { d = await api('/api/settings'); } catch (e) { return; }
  host.innerHTML = '';
  const write = (section, key, value) => apiPost('/api/settings', { section, key, value });
  const mkInput = (val, on) => { const i = el('input'); i.value = val; i.onchange = () => on(i.value.trim()); return i; };
  const mkSelect = (opts, val, on) => { const s = el('select'); opts.forEach(([v, t]) => { const o = el('option', null, t); o.value = v; s.appendChild(o); }); s.value = val; s.onchange = () => on(s.value); return s; };
  const boolOpts = [['True', '开启'], ['False', '关闭']];

  // 下载
  let s = el('div', 'sec'); s.appendChild(el('h3', null, '下载'));
  let f = el('div', 'frm');
  f.append(el('label', null, '缓存路径'), mkInput(d.downpath, v => write('downpath', 'downpath', v)));
  f.append(el('label', null, '自动下载'), mkSelect(boolOpts, d.autoDownload ? 'True' : 'False', v => write('down_list', 'auto_download', v)));
  f.append(el('label', null, '自动解压'), mkSelect(boolOpts, d.autoUnzip ? 'True' : 'False', v => write('down_list', 'auto_unzip', v)));
  f.append(el('label', null, '单文件线程数'), mkInput(d.downProc, v => write('down_list', 'download_processes', v)));
  f.append(el('label', null, '最低速度 (KB/s)'), mkInput(d.minSpeed, v => write('down_list', 'min_speed', v)));
  f.append(el('label', null, '速度限制 (KB/s)'), mkInput(d.speedLimit, v => write('down_list', 'speed_limit', v)));
  s.appendChild(f); host.appendChild(s);

  // 代理
  s = el('div', 'sec'); s.appendChild(el('h3', null, '代理')); f = el('div', 'frm');
  f.append(el('label', null, '开启'), mkSelect(boolOpts, d.proxy.open ? 'True' : 'False', v => write('proxy', 'openproxy', v)));
  f.append(el('label', null, '类型'), mkSelect([['http', 'http'], ['https', 'https'], ['socks5', 'socks5']], d.proxy.type, v => write('proxy', 'type', v)));
  f.append(el('label', null, '主机'), mkInput(d.proxy.host, v => write('proxy', 'host', v)));
  f.append(el('label', null, '端口'), mkInput(d.proxy.port, v => write('proxy', 'port', v)));
  s.appendChild(f); host.appendChild(s);

  // Debrid-Link
  s = el('div', 'sec'); s.appendChild(el('h3', null, 'Debrid-Link 下载中转站')); f = el('div', 'frm');
  // API Key 不回传明文：留空表示不修改；仅在填入新值时写入
  const keyInput = mkInput('', v => { if (v) write('debrid', 'api_key', v); });
  keyInput.type = 'password';
  keyInput.placeholder = d.debridKeySet ? '已设置（留空则不修改）' : '未设置';
  f.append(el('label', null, 'API Key'), keyInput);
  const testRow = el('div'); const testBtn = el('button', 'icon-btn', '测试'); const testRes = el('span'); testRes.style.marginLeft = '10px';
  testBtn.onclick = async () => { testRes.textContent = '测试中…'; const r = await apiPost('/api/debridtest', { key: keyInput.value.trim() }); testRes.textContent = r.ok ? '✓ 有效' : '✗ 无效'; testRes.style.color = r.ok ? '#4ade80' : '#f87171'; };
  testRow.append(testBtn, testRes);
  f.append(el('label', null, ''), testRow);
  s.appendChild(f); host.appendChild(s);

  // 系统
  s = el('div', 'sec'); s.appendChild(el('h3', null, '系统')); f = el('div', 'frm');
  f.append(el('label', null, '语言'), mkSelect(d.languages.map(l => [l.code, l.name]), d.language, v => write('language', 'lang', v)));
  f.append(el('label', null, '日志级别'), mkSelect([['info', 'info'], ['error', 'error'], ['debug', 'debug']], d.logLevel, v => write('loglevel', 'level', v)));
  f.append(el('label', null, '解压编码'), mkInput(d.encoding, v => write('encoding', 'encoding', v)));
  s.appendChild(f); host.appendChild(s);

  // 媒体库（可管理：新建/删除、加/移除文件夹、扫描导入）
  await renderMediaLibSection(host);

  // 外部访问（只读）
  s = el('div', 'sec'); s.appendChild(el('h3', null, '外部访问')); f = el('div', 'frm');
  f.append(el('label', null, '状态'), el('div', 'ro', d.web.enabled ? '已开启' : '已关闭'));
  f.append(el('label', null, '端口'), el('div', 'ro', String(d.web.port)));
  f.append(el('label', null, '访问密码'), el('div', 'ro', d.web.password ? '已设置' : '未设置'));
  s.appendChild(f);
  s.appendChild(el('div', 'note', '外部访问的开关 / 端口 / 密码请在桌面端修改（避免从外部改动后断开连接）。'));
  host.appendChild(s);
}

// ---------- 媒体库管理（设置页内）----------
let _libScanPoll = null;
async function renderMediaLibSection(host) {
  const sec = el('div', 'sec'); sec.appendChild(el('h3', null, '媒体库'));
  const bar = el('div', 'toolbar');
  const addLibBtn = el('button', 'icon-btn', '＋ 新建媒体库');
  const scanAllBtn = el('button', 'icon-btn', '↻ 扫描全部');
  bar.append(addLibBtn, scanAllBtn); sec.appendChild(bar);
  const status = el('div', 'note'); status.id = 'libScanStatus'; status.style.display = 'none'; sec.appendChild(status);
  const list = el('div'); sec.appendChild(list);
  sec.appendChild(el('div', 'note', '文件夹路径为运行本程序的电脑上的本地路径（如 D:\\ASMR）；添加后点"扫描"导入作品与元数据。删除媒体库不会删除本地文件、已导入记录保留。'));
  host.appendChild(sec);

  const refresh = async () => {
    let d; try { d = await api('/api/medialibs'); } catch (e) { return; }
    list.innerHTML = '';
    if (!d.libs.length) { list.appendChild(el('div', 'ro', '尚未配置媒体库，点击"新建媒体库"创建。')); return; }
    d.libs.forEach(l => list.appendChild(libCard(l, refresh)));
  };
  addLibBtn.onclick = async () => {
    const name = ((await uiPrompt('媒体库名称：', '', '新建媒体库')) || '').trim();
    if (!name) return;
    const r = await apiPost('/api/lib/create', { name });
    if (r.error) await uiAlert(r.error); else refresh();
  };
  scanAllBtn.onclick = async () => { await apiPost('/api/lib/scanall', {}); pollScan(); };
  await refresh();
  pollScan();   // 进入设置页即拉一次扫描状态（可能有后台扫描在进行）
}
function libCard(l, refresh) {
  const card = el('div', 'libcard');
  const head = el('div', 'lh');
  head.append(el('span', 'nm', l.name), el('span', 'ct', `${l.folders.length} 个文件夹`));
  const addF = el('button', 'mini', '添加文件夹');
  const scan = el('button', 'mini', '扫描');
  const rescan = el('button', 'mini', '重新扫描');
  const del = el('button', 'mini danger', '删除');
  head.append(addF, scan, rescan, del); card.appendChild(head);
  (l.folders || []).forEach(f => {
    const row = el('div', 'librow');
    const rm = el('button', 'mini danger', '移除');
    row.append(el('span', 'fp', f), rm); card.appendChild(row);
    rm.onclick = async () => { await apiPost('/api/lib/removefolder', { name: l.name, folder: f }); refresh(); };
  });
  addF.onclick = async () => {
    const folder = ((await uiPrompt('文件夹路径（本机绝对路径，如 D:\\ASMR）：', '', '添加文件夹')) || '').trim();
    if (!folder) return;
    const r = await apiPost('/api/lib/addfolder', { name: l.name, folder });
    if (r.error) await uiAlert(r.error); else refresh();
  };
  scan.onclick = async () => { await apiPost('/api/lib/scan', { name: l.name, force: false }); pollScan(); };
  rescan.onclick = async () => { if (await uiConfirm(`重新扫描"${l.name}"会强制重新抓取全部元数据，较慢。继续？`)) { await apiPost('/api/lib/scan', { name: l.name, force: true }); pollScan(); } };
  del.onclick = async () => { if (await uiConfirm(`确定删除媒体库"${l.name}"吗？\n不会删除本地文件，已导入的作品记录保留。`, { danger: true })) { await apiPost('/api/lib/delete', { name: l.name }); refresh(); } };
  return card;
}
// 扫描状态轮询：进行中时每 1.5s 刷新状态文本，结束或离开设置页即停
function pollScan() {
  const tick = async () => {
    const box = $('libScanStatus');
    if (!box || !document.body.contains(box)) { if (_libScanPoll) { clearInterval(_libScanPoll); _libScanPoll = null; } return; }
    let s; try { s = await api('/api/lib/scanstatus'); } catch (e) { return; }
    if (s.status) { box.style.display = ''; box.textContent = (s.scanning ? '⏳ ' : '✓ ') + s.status; }
    if (!s.scanning && _libScanPoll) { clearInterval(_libScanPoll); _libScanPoll = null; }
  };
  if (_libScanPoll) clearInterval(_libScanPoll);
  tick();
  _libScanPoll = setInterval(tick, 1500);
}
