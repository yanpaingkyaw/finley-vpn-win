'use strict';
const { contextBridge, ipcRenderer } = require('electron');
const invoke = (name, ...args) => ipcRenderer.invoke('finley:' + name, ...args).then(result => { if (!result.ok) throw new Error(result.error); return result.value; });
contextBridge.exposeInMainWorld('finley', Object.freeze({
  getState: () => invoke('getState'), addProfile: (key, name) => invoke('addProfile', key, name),
  renameProfile: (id, name) => invoke('renameProfile', id, name), removeProfile: id => invoke('removeProfile', id),
  selectProfile: id => invoke('selectProfile', id), connect: id => invoke('connect', id), disconnect: () => invoke('disconnect'),
  setBypass: (enabled, paths, domains, cidrs) => invoke('setBypass', enabled, paths, domains, cidrs), pickExecutable: () => invoke('pickExecutable'),
  setTheme: theme => invoke('setTheme', theme),
  onState: callback => { const listener = (_event, state) => callback(state); ipcRenderer.on('finley:state', listener); return () => ipcRenderer.removeListener('finley:state', listener); },
}));
