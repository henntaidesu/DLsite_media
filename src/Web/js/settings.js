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
  // 表单行：字符串→<label>，元素→原样放入。一行放多个相关字段，排布以桌面端 SettingsPage.xaml 为准
  const frow = (...kids) => {
    const r = el('div', 'frow');
    kids.forEach(k => r.appendChild(typeof k === 'string' ? el('label', null, k) : k));
    return r;
  };
  const frowTop = (...kids) => { const r = frow(...kids); r.classList.add('top'); return r; };   // 多行输入用
  const w = (node, px) => { node.style.width = px + 'px'; return node; };                        // 定宽控件（同桌面端）
  const grow = node => { node.classList.add('grow'); return node; };                             // 撑满剩余宽度

  // 下载（桌面端：缓存路径 / 自动下载+自动解压 / 线程数+最低速度+速度限制 / 解压密码库）
  let s = el('div', 'sec'); s.appendChild(el('h3', null, '下载'));
  s.appendChild(frow('缓存路径', grow(mkInput(d.downpath, v => write('downpath', 'downpath', v)))));
  s.appendChild(frow(
    '自动下载', w(mkSelect(boolOpts, d.autoDownload ? 'True' : 'False', v => write('down_list', 'auto_download', v)), 120),
    '自动解压', w(mkSelect(boolOpts, d.autoUnzip ? 'True' : 'False', v => write('down_list', 'auto_unzip', v)), 120)));
  s.appendChild(frow(
    '单文件线程数', w(mkInput(d.downProc, v => write('down_list', 'download_processes', v)), 80),
    '最低速度 (KB/s)', w(mkInput(d.minSpeed, v => write('down_list', 'min_speed', v)), 80),
    '速度限制 (KB/s)', w(mkInput(d.speedLimit, v => write('down_list', 'speed_limit', v)), 80)));
  // 解压密码库：遇到加密压缩包时按这里的密码逐条试（一行一个，失焦即存）
  const pwBox = el('textarea'); pwBox.value = d.unzipPasswords || '';
  pwBox.onchange = () => write('unzip', 'passwords', pwBox.value);
  const pwWrap = grow(el('div'));
  pwWrap.append(pwBox, el('div', 'note', '一行一个密码，解压加密压缩包时按顺序尝试'));
  s.appendChild(frowTop('解压密码库', pwWrap));
  host.appendChild(s);

  // 代理（桌面端：开关 / 类型 / 主机 / 端口 同一行，只有第一个有标签）
  s = el('div', 'sec'); s.appendChild(el('h3', null, '代理'));
  const proxyHost = grow(mkInput(d.proxy.host, v => write('proxy', 'host', v)));
  proxyHost.placeholder = '主机';
  const proxyPort = w(mkInput(d.proxy.port, v => write('proxy', 'port', v)), 80);
  proxyPort.placeholder = '端口';
  s.appendChild(frow(
    '开启', w(mkSelect(boolOpts, d.proxy.open ? 'True' : 'False', v => write('proxy', 'openproxy', v)), 120),
    w(mkSelect([['http', 'http'], ['https', 'https'], ['socks5', 'socks5']], d.proxy.type, v => write('proxy', 'type', v)), 100),
    proxyHost, proxyPort));
  host.appendChild(s);

  // Debrid-Link（桌面端：API Key 与「测试」同一行）
  s = el('div', 'sec'); s.appendChild(el('h3', null, 'Debrid-Link 下载中转站'));
  // API Key 不回传明文：留空表示不修改；仅在填入新值时写入
  const keyInput = grow(mkInput('', v => { if (v) write('debrid', 'api_key', v); }));
  keyInput.type = 'password';
  keyInput.placeholder = d.debridKeySet ? '已设置（留空则不修改）' : '未设置';
  const testBtn = el('button', 'icon-btn', '测试');
  const testRes = el('span', 'tres');
  testBtn.onclick = async () => { testRes.textContent = '测试中…'; const r = await apiPost('/api/debridtest', { key: keyInput.value.trim() }); testRes.textContent = r.ok ? '✓ 有效' : '✗ 无效'; testRes.style.color = r.ok ? '#4ade80' : '#f87171'; };
  s.appendChild(frow('API Key', keyInput, testBtn, testRes));
  host.appendChild(s);

  // 系统（桌面端：语言 + 日志级别 + 解压编码 同一行；「关闭按钮」为桌面独有项）
  s = el('div', 'sec'); s.appendChild(el('h3', null, '系统'));
  s.appendChild(frow(
    '语言', w(mkSelect(d.languages.map(l => [l.code, l.name]), d.language, v => write('language', 'lang', v)), 140),
    '日志级别', w(mkSelect([['info', 'info'], ['error', 'error'], ['debug', 'debug']], d.logLevel, v => write('loglevel', 'level', v)), 120),
    '解压编码', w(mkInput(d.encoding, v => write('encoding', 'encoding', v)), 120)));
  host.appendChild(s);

  // 媒体库（可管理：新建/删除、加/移除文件夹、扫描导入）
  await renderMediaLibSection(host);

  // 图床存储：作品卡封面改由图床直供。媒体库多放在 HDD 上，翻一页卡片要逐张唤醒磁盘随机读，
  // 封面搬到图床后浏览器直接向图床要 ?w= 缩略图，本机硬盘只在真正播放作品时才转。
  // 排布同桌面端：启用+项目 一行 / 服务地址 一行 / API Token 与「测试连接」「迁移封面」同一行
  s = el('div', 'sec'); s.appendChild(el('h3', null, '图床存储'));
  const ihProj = w(mkInput(d.imageHost.project, v => write('image_host', 'project', v)), 180);
  ihProj.placeholder = '项目标识（slug）';
  s.appendChild(frow(
    '启用', w(mkSelect(boolOpts, d.imageHost.enabled ? 'True' : 'False', v => write('image_host', 'enabled', v)), 120),
    '项目', ihProj));
  const ihUrl = grow(mkInput(d.imageHost.baseUrl, v => write('image_host', 'base_url', v)));
  ihUrl.placeholder = 'http://192.168.1.5:9990';
  s.appendChild(frow('服务地址', ihUrl));
  // Token 同 debrid：不回传明文，留空表示不修改
  const ihToken = grow(mkInput('', v => { if (v) write('image_host', 'token', v); }));
  ihToken.type = 'password';
  ihToken.placeholder = d.imageHost.tokenSet ? '已设置（留空则不修改）' : '未设置';
  const ihTest = el('button', 'icon-btn', '测试连接');
  const ihSync = el('button', 'icon-btn', '迁移封面');
  const ihRes = el('span', 'tres');
  // 三个值都从输入框现取：设置页是失焦即存的，刚输入的值可能还没写完库
  ihTest.onclick = async () => {
    ihRes.style.color = ''; ihRes.textContent = '测试中…';
    const r = await apiPost('/api/imagehost/test', { baseUrl: ihUrl.value.trim(), project: ihProj.value.trim(), token: ihToken.value.trim() });
    ihRes.textContent = r.ok ? `✓ 已连接（图床现有 ${r.count} 张）` : `✗ ${r.error || '连接失败'}`;
    ihRes.style.color = r.ok ? '#4ade80' : '#f87171';
  };
  // 重复点不会跑两轮（服务端单实例串行），同一个作品也不会被迁移两次
  ihSync.onclick = async () => { await apiPost('/api/imagehost/migrate', {}); pollImageHost(); };
  s.appendChild(frow('API Token', ihToken, ihTest, ihSync, ihRes));
  const ihStatus = el('div', 'note'); ihStatus.id = 'imgHostStatus';
  s.appendChild(ihStatus);
  s.appendChild(el('div', 'note', '开启后作品卡封面由图床提供，未迁移的封面自动回退本地硬盘。迁移只复制不删除本地原图（它也是详情页的第一张图）；同一个作品重复迁移会被跳过，中断后再点一次即可续传。手机要看到图，服务地址须填电脑的局域网地址（不能是 127.0.0.1），并在图床「系统设置 → 附加访问主机名」里放行该地址。'));
  host.appendChild(s);
  pollImageHost();

  // 外部访问（只读，三项同一行，同桌面端那一行 外部访问 / 端口 / 访问密码）
  s = el('div', 'sec'); s.appendChild(el('h3', null, '外部访问'));
  s.appendChild(frow(
    '外部访问', el('div', 'ro', d.web.enabled ? '已开启' : '已关闭'),
    '端口', el('div', 'ro', String(d.web.port)),
    '访问密码', el('div', 'ro', d.web.password ? '已设置' : '未设置')));
  s.appendChild(el('div', 'note', '外部访问的开关 / 端口 / 密码请在桌面端修改（避免从外部改动后断开连接）。'));
  host.appendChild(s);
}

// 图床迁移状态轮询：迁移中每 1.5s 刷新，结束或离开设置页即停（同扫描状态的做法）
let _imgHostPoll = null;
function pollImageHost() {
  const tick = async () => {
    const box = $('imgHostStatus');
    if (!box || !document.body.contains(box)) { if (_imgHostPoll) { clearInterval(_imgHostPoll); _imgHostPoll = null; } return; }
    let s; try { s = await api('/api/imagehost/status'); } catch (e) { return; }
    // 常驻提示（已迁移/待迁移）由服务端给，桌面端共用同一句文案
    box.textContent = s.running ? '⏳ 迁移中 ' + s.status : (s.status ? '✓ ' + s.status : s.idle);
    if (!s.running && _imgHostPoll) { clearInterval(_imgHostPoll); _imgHostPoll = null; }
  };
  if (_imgHostPoll) clearInterval(_imgHostPoll);
  tick();
  _imgHostPoll = setInterval(tick, 1500);
}

// ---------- 媒体库管理（设置页内）----------
let _libScanPoll = null;
async function renderMediaLibSection(host) {
  const sec = el('div', 'sec'); sec.appendChild(el('h3', null, '媒体库'));
  const bar = el('div', 'toolbar inline');
  // 按钮文案同桌面端 MediaLibSettingDialog.BuildSection
  const addLibBtn = el('button', 'icon-btn', '新建媒体库');
  const scanAllBtn = el('button', 'icon-btn', '扫描全部数据源');
  bar.append(addLibBtn, scanAllBtn); sec.appendChild(bar);
  const status = el('div', 'note'); status.id = 'libScanStatus'; status.style.display = 'none'; sec.appendChild(status);
  const list = el('div'); sec.appendChild(list);
  sec.appendChild(el('div', 'note', '文件夹路径为运行本程序的电脑上的本地路径（如 D:\\ASMR）；添加后点"扫描元数据"导入作品与元数据。删除媒体库不会删除本地文件、已导入记录保留。'));
  host.appendChild(sec);

  const refresh = async () => {
    let d; try { d = await api('/api/medialibs'); } catch (e) { return; }
    list.innerHTML = '';
    if (!d.libs.length) { list.appendChild(el('div', 'ro', '还没有媒体库，点击"新建媒体库"创建。')); return; }
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
  const scan = el('button', 'mini', '扫描元数据');
  const rescan = el('button', 'mini', '重新扫描元数据');
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
