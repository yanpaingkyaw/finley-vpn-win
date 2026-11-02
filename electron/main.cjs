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
let win, data, status = 'disconnected', error = null, quitting = false, lifecycleCommand = 0;
const primaryInstance = app.requestSingleInstanceLock();
if (!primaryInstance) app.quit();
app.on('second-instance', () => { if (win) { if (win.isMinimized()) win.restore(); win.focus(); } });
const installedBin = path.join(process.env.ProgramFiles || 'C:\Program Files', 'Finley', 'resources', 'bin');
const developmentBin = path.join(process.env.ProgramFiles || 'C:\Program Files', 'Finley Development', 'resources', 'bin');
const clientPath = app.isPackaged ? path.join(process.resourcesPath, 'bin', 'finley-service-client.exe') : path.join(fs.existsSync(path.join(installedBin, 'finley-service-client.exe')) ? installedBin : developmentBin, 'finley-service-client.exe');
const network = createNetworkController({clientPath, onStatus: value => {
 if ((status === 'connecting' && value.state === 'disconnected' && !value.owned) || (status === 'disconnecting' && value.state === 'connected' && value.owned)) return;
 status = value.state; error = value.error; broadcast();
}});
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
 register('removeProfile', async id => { requireProfile(id); if (data.selectedProfileId === id && (status === 'connecting' || status === 'disconnecting')) throw new Error('Wait for the connection change to finish before removing this server.'); if (data.selectedProfileId === id && network.getStatus().owned) { ++lifecycleCommand; await network.disconnect(); status = 'disconnected'; error = null; } data.profiles = data.profiles.filter(p => p.id !== id); if (data.selectedProfileId === id) data.selectedProfileId = data.profiles[0]?.id || null; save(); broadcast(); });
 register('selectProfile', id => { requireProfile(id); if (network.getStatus().owned || status === 'connecting' || status === 'disconnecting') throw new Error('Disconnect before changing servers.'); data.selectedProfileId = id; error = null; status = 'disconnected'; save(); broadcast(); });
 register('connect', async id => { const profile = requireProfile(id); if (status === 'connecting' || status === 'connected' || status === 'disconnecting') throw new Error('A connection is already active or changing.'); const command = ++lifecycleCommand; status = 'connecting'; error = null; broadcast(); try { if (!safeStorage.isEncryptionAvailable()) throw new Error('Windows secure storage is unavailable.'); let secret; try { secret = JSON.parse(safeStorage.decryptString(Buffer.from(profile.secret, 'base64'))); } catch { throw new Error('Could not unlock the saved access key for this Windows account.'); } await network.connect({server: profile.server, method: profile.method, port: secret.port, password: secret.password}, data.bypass); if (command === lifecycleCommand) { status = network.getStatus().state; error = network.getStatus().error; } } catch (e) { if (command === lifecycleCommand) { status = 'error'; error = e.message; } throw e; } finally { broadcast(); } });
 register('disconnect', async () => { ++lifecycleCommand; status = 'disconnecting'; broadcast(); try { await network.disconnect(); status = network.getStatus().state; error = network.getStatus().error; } catch { status = 'error'; error = 'Could not clean up the connection. Restart Finley and check your network settings.'; throw new Error(error); } finally { broadcast(); } });
 register('setTheme', theme => { if (!['light','dark'].includes(theme)) throw new Error('Invalid theme.'); data.theme = theme; save(); broadcast(); });
 register('setBypass', (enabled, paths, domains, cidrs) => { data.bypass = normalizeBypass(enabled, paths, domains, cidrs); save(); broadcast(); });
 register('pickExecutable', async () => { const result = await dialog.showOpenDialog(win, { title: 'Choose an application', properties: ['openFile'], filters: [{name:'Windows applications', extensions:['exe']}] }); return result.canceled ? null : result.filePaths[0]; });
}
app.whenReady().then(async () => { if (!primaryInstance) return; try { Menu.setApplicationMenu(null); data = load(); await network.reconcile(); setup(); win = new BrowserWindow({icon:path.join(__dirname, app.isPackaged ? '../dist/finley-icon.png' : '../public/finley-icon.png'),width:1180,height:820,minWidth:780,minHeight:650,backgroundColor:'#f6f9fd',show:false,webPreferences:{preload:path.join(__dirname,'preload.cjs'),contextIsolation:true,nodeIntegration:false,sandbox:true}}); win.once('ready-to-show', () => win.show()); win.webContents.setWindowOpenHandler(() => ({action:'deny'})); win.webContents.on('will-navigate', e => e.preventDefault()); if (process.env.FINLEY_DEV_URL && !app.isPackaged) win.loadURL(process.env.FINLEY_DEV_URL); else win.loadFile(path.join(__dirname,'../dist/index.html')); } catch (e) { dialog.showErrorBox('Finley could not start', e.message); app.quit(); } });
app.on('window-all-closed', () => app.quit());
app.on('before-quit', event => { if (quitting) return; event.preventDefault(); quitting = true; (network.getStatus().owned ? network.disconnect() : Promise.resolve()).catch(() => { dialog.showErrorBox('Finley cleanup', 'Finley could not confirm that the VPN stopped cleanly. Check your network settings.'); }).finally(() => { network.close(); app.quit(); }); });
