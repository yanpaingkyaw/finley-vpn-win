'use strict';
const { app, BrowserWindow, Menu, ipcMain, dialog, safeStorage } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const crypto = require('node:crypto');
// Keep saved profiles consistent between development and installed builds.
app.setPath('userData', path.join(app.getPath('appData'), 'finley-vpn'));
const { parseKey } = require('./key.cjs');
const { normalizeBypass } = require('./bypass.cjs');
const { createNetworkController } = require('./network.cjs');
let win, data, status = 'disconnected', error = null, quitting = false;
const binDir = app.isPackaged ? path.join(process.resourcesPath, 'bin') : path.join(__dirname, '..', 'bin');
const network = createNetworkController({enginePath: path.join(binDir, 'sing-box.exe'), helperPath: path.join(binDir, 'finley-tunnel-helper.exe'), onUnexpectedExit: () => { if (status === 'connected') { status = 'error'; error = 'The VPN engine stopped unexpectedly. Traffic may use your regular connection.'; broadcast(); } }});
const defaults = () => ({ profiles: [], selectedProfileId: null, bypass: { enabled: false, paths: [], domains: [], cidrs: [] }, theme: 'light' });
function storePath() { return path.join(app.getPath('userData'), 'profiles.json'); }
function load() { try { const parsed = JSON.parse(fs.readFileSync(storePath(), 'utf8')); if (!Array.isArray(parsed.profiles) || !parsed.bypass || !Array.isArray(parsed.bypass.paths)) throw new Error('bad storage'); parsed.bypass = normalizeBypass(parsed.bypass.enabled, parsed.bypass.paths, parsed.bypass.domains ?? [], parsed.bypass.cidrs ?? []); return parsed; } catch (e) { if (e.code === 'ENOENT') return defaults(); throw new Error('Finley could not read its saved settings. Restore the settings file or contact support.'); } }
function save() { const temp = storePath() + '.tmp'; fs.writeFileSync(temp, JSON.stringify(data, null, 2), { mode: 0o600 }); fs.renameSync(temp, storePath()); }
function state() { return { version: app.getVersion(), profiles: data.profiles.map(({id,name,server,method}) => ({id,name,server,method})), selectedProfileId: data.selectedProfileId, bypass: data.bypass, theme: data.theme, status, error }; }
function broadcast() { if (win && !win.isDestroyed()) win.webContents.send('finley:state', state()); }
function requireProfile(id) { const p = data.profiles.find(x => x.id === id); if (!p) throw new Error('Server not found.'); return p; }
function validName(name) { if (typeof name !== 'string' || !name.trim() || name.trim().length > 100) throw new Error('Enter a server name of up to 100 characters.'); return name.trim(); }
function register(name, handler) { ipcMain.handle('finley:' + name, async (event, ...args) => { if (!win || event.sender !== win.webContents || event.senderFrame !== win.webContents.mainFrame) return {ok:false,error:'Unauthorized request.'}; try { const value = await handler(...args); return {ok:true,value}; } catch (e) { const message = e instanceof Error ? e.message : 'An unexpected error occurred.'; return {ok:false,error:message}; } }); }
function setup() {
 register('getState', () => state());
 register('addProfile', (key, name) => { const parsed = parseKey(key); const label = validName(name || parsed.label || 'My server'); if (!safeStorage.isEncryptionAvailable()) throw new Error('Windows secure storage is unavailable. Your key was not saved.'); const secret = safeStorage.encryptString(JSON.stringify({password:parsed.password,port:parsed.port})).toString('base64'); const id = crypto.randomUUID(); data.profiles.push({id,name:label,server:parsed.server,method:parsed.method,secret}); if (!data.selectedProfileId) data.selectedProfileId = id; save(); broadcast(); });
 register('renameProfile', (id, name) => { requireProfile(id).name = validName(name); save(); broadcast(); });
 register('removeProfile', async id => { requireProfile(id); if (data.selectedProfileId === id && status !== 'disconnected') { await network.disconnect(); status = 'disconnected'; error = null; } data.profiles = data.profiles.filter(p => p.id !== id); if (data.selectedProfileId === id) data.selectedProfileId = data.profiles[0]?.id || null; save(); broadcast(); });
 register('selectProfile', id => { requireProfile(id); if (status === 'connected' || status === 'connecting') throw new Error('Disconnect before changing servers.'); data.selectedProfileId = id; error = null; status = 'disconnected'; save(); broadcast(); });
 register('connect', async id => { const profile = requireProfile(id); if (status === 'connecting' || status === 'connected' || status === 'disconnecting') throw new Error('A connection is already active or changing.'); status = 'connecting'; error = null; broadcast(); try { if (!safeStorage.isEncryptionAvailable()) throw new Error('Windows secure storage is unavailable.'); let secret; try { secret = JSON.parse(safeStorage.decryptString(Buffer.from(profile.secret, 'base64'))); } catch { throw new Error('Could not unlock the saved access key for this Windows account.'); } await network.connect({server: profile.server, method: profile.method, port: secret.port, password: secret.password}, data.bypass); status = 'connected'; } catch (e) { status = 'error'; error = e.message; throw e; } finally { broadcast(); } });
 register('disconnect', async () => { status = 'disconnecting'; broadcast(); try { await network.disconnect(); status = 'disconnected'; error = null; } catch { status = 'error'; error = 'Could not clean up the connection. Restart Finley and check your network settings.'; throw new Error(error); } finally { broadcast(); } });
 register('setTheme', theme => { if (!['light','dark'].includes(theme)) throw new Error('Invalid theme.'); data.theme = theme; save(); broadcast(); });
 register('setBypass', (enabled, paths, domains, cidrs) => { data.bypass = normalizeBypass(enabled, paths, domains, cidrs); save(); broadcast(); });
 register('pickExecutable', async () => { const result = await dialog.showOpenDialog(win, { title: 'Choose an application', properties: ['openFile'], filters: [{name:'Windows applications', extensions:['exe']}] }); return result.canceled ? null : result.filePaths[0]; });
}
app.whenReady().then(() => { try { Menu.setApplicationMenu(null); data = load(); setup(); win = new BrowserWindow({icon:path.join(__dirname, app.isPackaged ? '../dist/finley-icon.png' : '../public/finley-icon.png'),width:1180,height:820,minWidth:780,minHeight:650,backgroundColor:'#f6f9fd',show:false,webPreferences:{preload:path.join(__dirname,'preload.cjs'),contextIsolation:true,nodeIntegration:false,sandbox:true}}); win.once('ready-to-show', () => win.show()); win.webContents.setWindowOpenHandler(() => ({action:'deny'})); win.webContents.on('will-navigate', e => e.preventDefault()); if (process.env.FINLEY_DEV_URL && !app.isPackaged) win.loadURL(process.env.FINLEY_DEV_URL); else win.loadFile(path.join(__dirname,'../dist/index.html')); } catch (e) { dialog.showErrorBox('Finley could not start', e.message); app.quit(); } });
app.on('window-all-closed', () => app.quit());
app.on('before-quit', event => { if (quitting) return; event.preventDefault(); quitting = true; network.disconnect().catch(() => { dialog.showErrorBox('Finley cleanup', 'Finley could not confirm that the VPN stopped cleanly. Check your network settings.'); }).finally(() => app.quit()); });
