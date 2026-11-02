import finleyIcon from './assets/finley-icon.svg';
import { useCallback, useEffect, useState } from 'react';
import { Home, Server, Grid2X2, ShieldCheck, Sun, Moon, ChevronRight, Plus, X, Trash2, Pencil, FolderOpen, AlertCircle, Check, Power, LoaderCircle, Info } from 'lucide-react';
import type { AppState, Profile } from './types';

type Page = 'home' | 'servers' | 'bypass' | 'about';
const displayName = (path: string) => path.split(/[\\/]/).pop() || path;

export default function App() {
  const [state, setState] = useState<AppState | null>(null);
  const [page, setPage] = useState<Page>('home');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const [dialog, setDialog] = useState<'add' | 'rename' | 'remove' | null>(null);
  const [target, setTarget] = useState<Profile | null>(null);
  const [name, setName] = useState('');
  const [key, setKey] = useState('');
  const [showKey, setShowKey] = useState(false);
  const [dialogError, setDialogError] = useState('');
  const [domain, setDomain] = useState('');
  const [cidr, setCidr] = useState('');

  const refresh = useCallback(async () => setState(await window.finley.getState()), []);
  useEffect(() => {
    if (!window.finley) { setMessage('Finley desktop services are unavailable. Open the installed app to continue.'); return; }
    void refresh().catch(() => setMessage('Could not load Finley settings.'));
    return window.finley.onState(setState);
  }, [refresh]);
  useEffect(() => { if (state) document.documentElement.dataset.theme = state.theme; }, [state]);
  const action = async (fn: () => Promise<void>, inDialog = false) => {
    setBusy(true); setMessage(''); setDialogError('');
    try { await fn(); await refresh(); return true; }
    catch (error) { const text = error instanceof Error ? error.message : 'Something went wrong. Please try again.'; if (inDialog) setDialogError(text); else setMessage(text); return false; }
    finally { setBusy(false); }
  };
  const selected = state?.profiles.find(p => p.id === state.selectedProfileId);
  const active = state?.status === 'connected';
  const changing = busy || state?.status === 'connecting' || state?.status === 'disconnecting';
  const toggleConnection = () => {
    if (!state) return;
    if (active) void action(() => window.finley.disconnect());
    else if (selected) void action(() => window.finley.connect(selected.id));
    else setPage('servers');
  };
  const openAdd = () => { setName(''); setKey(''); setShowKey(false); setDialogError(''); setDialog('add'); };
  const openRename = (profile: Profile) => { setTarget(profile); setName(profile.name); setDialogError(''); setDialog('rename'); };
  const openRemove = (profile: Profile) => { setTarget(profile); setDialogError(''); setDialog('remove'); };
  const saveDialog = async () => {
    let ok = false;
    if (dialog === 'add') ok = await action(() => window.finley.addProfile(key.trim(), name.trim()), true);
    if (dialog === 'rename' && target) ok = await action(() => window.finley.renameProfile(target.id, name.trim()), true);
    if (dialog === 'remove' && target) ok = await action(() => window.finley.removeProfile(target.id), true);
    if (ok) { setDialog(null); setKey(''); }
  };
  const updateBypass = (changes: Partial<AppState['bypass']>) => {
    if (!state) return Promise.resolve();
    const next = { ...state.bypass, ...changes };
    return window.finley.setBypass(next.enabled, next.paths, next.domains, next.cidrs);
  };
  const addApp = async () => {
    if (!state) return;
    await action(async () => { const path = await window.finley.pickExecutable(); if (path) await updateBypass({ paths: [...state.bypass.paths, path] }); });
  };
  const addRule = async (type: 'domains' | 'cidrs') => {
    if (!state) return;
    const value = (type === 'domains' ? domain : cidr).trim();
    if (!value) return;
    const ok = await action(() => updateBypass({ [type]: [...state.bypass[type], value] }));
    if (ok) { if (type === 'domains') setDomain(''); else setCidr(''); }
  };
  if (!state) return <div className="loading"><img className="brand-mark" src={finleyIcon} alt="" /><p>{message || 'Loading Finley...'}</p></div>;
  const statusText: Record<AppState['status'], string> = { disconnected: 'Disconnected', connecting: 'Connecting...', connected: 'Connected', disconnecting: 'Disconnecting...', error: 'Connection failed' };
  return <div className="app-shell">
    <aside className="sidebar">
      <div className="brand"><img className="brand-mark" src={finleyIcon} alt="" /><span>Finley</span></div>
      <nav aria-label="Main navigation">
        <button className={page === 'home' ? 'nav active' : 'nav'} onClick={() => setPage('home')}><Home size={21}/><span>Home</span></button>
        <button className={page === 'servers' ? 'nav active' : 'nav'} onClick={() => setPage('servers')}><Server size={21}/><span>Servers</span></button>
        <button className={page === 'bypass' ? 'nav active' : 'nav'} onClick={() => setPage('bypass')}><Grid2X2 size={21}/><span>Bypass</span></button>
        <button className={page === 'about' ? 'nav active' : 'nav'} onClick={() => setPage('about')}><Info size={21}/><span>About us</span></button>
      </nav>
      <div className="theme"><span className="theme-label">Theme</span><div className="theme-switch" role="group" aria-label="Color theme">
        <button aria-pressed={state.theme === 'light'} className={state.theme === 'light' ? 'selected' : ''} onClick={() => void action(() => window.finley.setTheme('light'))}><Sun size={18}/> Light</button>
        <button aria-pressed={state.theme === 'dark'} className={state.theme === 'dark' ? 'selected' : ''} onClick={() => void action(() => window.finley.setTheme('dark'))}><Moon size={18}/> Dark</button>
      </div></div>
    </aside>
    <main className="main">
      <div className="drag-area" aria-hidden="true" />
      {(message || state.error) && <div className="alert" role="alert"><AlertCircle size={19}/><span>{message || state.error}</span>{message && <button aria-label="Dismiss message" onClick={() => setMessage('')}><X size={17}/></button>}</div>}
      {page === 'home' && <section className="home-page" aria-label="Connection">
        <div className={'status-orb ' + (active ? 'online' : '')}>{changing ? <LoaderCircle className="spin" size={46}/> : active ? <ShieldCheck size={49}/> : <Power size={45}/>}</div>
        <h1 className="status-title" aria-live="polite">{statusText[state.status]}</h1>
        <p className="status-hint">{active ? 'Your VPN connection is active.' : selected ? 'Connect when you are ready.' : 'Add a server to get started.'}</p>
        <button className="primary connect" disabled={changing} onClick={toggleConnection}>{changing ? 'Please wait...' : active ? 'Disconnect' : selected ? 'Connect' : 'Add a server'}</button>
        <div className="info-card">
          <button className="info-row" onClick={() => setPage('servers')}><span className="icon-circle"><Server size={23}/></span><span className="info-text"><small>SERVER</small><strong>{selected?.name || 'Choose a server'}</strong></span><ChevronRight size={20}/></button>
          <div className="divider"/>
          <div className="info-row"><span className="icon-circle"><ShieldCheck size={23}/></span><span className="info-text"><small>CONFIGURED PROTOCOL</small><strong>{selected ? 'Shadowsocks' : 'No server selected'}</strong>{selected?.method && <span className="detail">{selected.method}</span>}</span></div>
        </div>
        <button className="bypass-card" onClick={() => setPage('bypass')}><span className="icon-circle accent"><Grid2X2 size={23}/></span><span className="info-text"><small>BYPASS</small><strong>{state.bypass.enabled ? `${state.bypass.paths.length + state.bypass.domains.length + state.bypass.cidrs.length} rules selected` : 'Bypass is off'}</strong><span className="detail">Selected traffic uses your regular connection</span></span><span className="manage">Manage</span><ChevronRight size={20}/></button>
      </section>}
      {page === 'servers' && <section className="content-page">
        <div className="page-heading"><div><p className="eyebrow">YOUR CONNECTIONS</p><h1>Servers</h1><p>Manage your saved Outline access keys.</p></div><button className="primary compact" onClick={openAdd}><Plus size={18}/> Add server</button></div>
        {state.profiles.length === 0 ? <div className="empty"><span className="icon-circle large"><Server size={27}/></span><h2>No servers yet</h2><p>Add an access key from your Outline server to get started.</p><button className="primary compact" onClick={openAdd}><Plus size={18}/> Add server</button></div> : <div className="list-card">{state.profiles.map(profile => <div className="server-item" key={profile.id}><span className="icon-circle"><Server size={21}/></span><div className="item-details"><strong>{profile.name}</strong><span>{profile.server} · Shadowsocks</span></div>{profile.id === state.selectedProfileId ? <span className="badge"><Check size={14}/> Selected</span> : <button className="secondary small" disabled={changing || active} onClick={() => void action(() => window.finley.selectProfile(profile.id))}>Select</button>}<button className="icon-button" aria-label={`Rename ${profile.name}`} onClick={() => openRename(profile)}><Pencil size={17}/></button><button className="icon-button" aria-label={`Remove ${profile.name}`} onClick={() => openRemove(profile)}><Trash2 size={17}/></button></div>)}</div>}
        {active && <p className="footnote">Disconnect before switching servers.</p>}
      </section>}
      {page === 'about' && <section className="content-page" aria-labelledby="about-title">
        <div className="page-heading"><div><p className="eyebrow">GET TO KNOW FINLEY</p><h1 id="about-title">About us</h1><p>Simple VPN access for Windows.</p></div></div>
        <div className="about-card"><div className="about-brand"><img src={finleyIcon} alt=""/><div><h2>Finley</h2><p>Version {state.version}</p></div></div><p>Finley is a Windows VPN client that connects to your existing Outline-compatible server using a Shadowsocks access key.</p><p>Choose applications, domains, or IPv4 addresses that should use your regular internet connection with bypass rules.</p></div>
      </section>}
      {page === 'bypass' && <section className="content-page">
        <div className="page-heading"><div><p className="eyebrow">YOUR PREFERENCES</p><h1>Bypass</h1><p>Choose which apps or destinations use your regular connection.</p></div></div>
        <div className="setting-card"><div><h2>Bypass selected traffic</h2><p>Matching apps and destinations use your regular internet connection instead of the VPN.</p></div><button className={'toggle ' + (state.bypass.enabled ? 'on' : '')} role="switch" aria-checked={state.bypass.enabled} aria-label="Enable bypass" disabled={changing} onClick={() => void action(() => updateBypass({enabled: !state.bypass.enabled}))}><span/></button></div>
        <div className="notice"><AlertCircle size={19}/><span>Bypassed traffic uses your regular connection. DNS is handled by Finley. Reconnect to apply changes.</span></div>
        <div className="section-heading"><div><h2>Applications</h2><p>Choose an executable file for each app.</p></div><button className="secondary" disabled={changing} onClick={() => void addApp()}><Plus size={17}/> Add app</button></div>
        {state.bypass.paths.length === 0 ? <div className="empty small-empty"><FolderOpen size={28}/><h2>No apps added</h2><p>Add an application to bypass the VPN.</p></div> : <div className="list-card">{state.bypass.paths.map(path => <div className="server-item" key={path}><span className="icon-circle"><Grid2X2 size={21}/></span><div className="item-details"><strong>{displayName(path)}</strong><span className="path">{path}</span></div><button className="icon-button" disabled={changing} aria-label={`Remove ${displayName(path)}`} onClick={() => void action(() => updateBypass({paths: state.bypass.paths.filter(p => p !== path)}))}><Trash2 size={18}/></button></div>)}</div>}
        <div className="rule-section"><div className="section-heading"><div><h2>Domains</h2><p>Include the domain and its subdomains, such as example.com and mail.example.com. Encrypted or hidden hostnames and unsupported protocols may remain on the VPN.</p></div></div>
          <form className="rule-form" onSubmit={e => {e.preventDefault(); void addRule('domains');}}><label className="sr-only" htmlFor="bypass-domain">Domain to bypass</label><input id="bypass-domain" value={domain} onChange={e => setDomain(e.target.value)} placeholder="example.com" autoComplete="off"/><button className="secondary" disabled={changing || !domain.trim()}><Plus size={17}/> Add domain</button></form>
          {state.bypass.domains.length === 0 ? <p className="rule-empty">No domains added.</p> : <div className="list-card">{state.bypass.domains.map(value => <div className="server-item" key={value}><div className="item-details"><strong>{value}</strong><span>Includes subdomains</span></div><button className="icon-button" disabled={changing} aria-label={`Remove domain ${value}`} onClick={() => void action(() => updateBypass({domains: state.bypass.domains.filter(x => x !== value)}))}><Trash2 size={18}/></button></div>)}</div>}
        </div>
        <div className="rule-section"><div className="section-heading"><div><h2>IP addresses and ranges</h2><p>Enter an IPv4 address or CIDR range, for example 192.0.2.1 or 192.0.2.0/24.</p></div></div>
          <form className="rule-form" onSubmit={e => {e.preventDefault(); void addRule('cidrs');}}><label className="sr-only" htmlFor="bypass-cidr">IP address or CIDR range to bypass</label><input id="bypass-cidr" value={cidr} onChange={e => setCidr(e.target.value)} placeholder="192.0.2.0/24" autoComplete="off"/><button className="secondary" disabled={changing || !cidr.trim()}><Plus size={17}/> Add IP range</button></form>
          {state.bypass.cidrs.length === 0 ? <p className="rule-empty">No IP addresses or ranges added.</p> : <div className="list-card">{state.bypass.cidrs.map(value => <div className="server-item" key={value}><div className="item-details"><strong>{value}</strong></div><button className="icon-button" disabled={changing} aria-label={`Remove IP rule ${value}`} onClick={() => void action(() => updateBypass({cidrs: state.bypass.cidrs.filter(x => x !== value)}))}><Trash2 size={18}/></button></div>)}</div>}
        </div>
      </section>}
    </main>
    {dialog && <div className="modal-overlay" onMouseDown={event => { if (event.target === event.currentTarget && !busy) setDialog(null); }}><div className="modal" role="dialog" aria-modal="true" aria-labelledby="dialog-title"><button className="modal-close icon-button" aria-label="Close" disabled={busy} onClick={() => setDialog(null)}><X size={20}/></button>
      <h2 id="dialog-title">{dialog === 'add' ? 'Add a server' : dialog === 'rename' ? 'Rename server' : 'Remove server?'}</h2>
      {dialog === 'add' && <><p>Paste a standard Outline access key to add your server.</p><label htmlFor="server-name">Server name <span className="muted">(optional)</span></label><input id="server-name" value={name} onChange={e => setName(e.target.value)} placeholder="My server" maxLength={80} autoFocus/><label htmlFor="access-key">Access key</label><div className="key-field"><input id="access-key" type={showKey ? 'text' : 'password'} autoComplete="off" value={key} onChange={e => setKey(e.target.value)} placeholder="ss://..."/><button type="button" onClick={() => setShowKey(!showKey)}>{showKey ? 'Hide' : 'Show'}</button></div><p className="field-note">Your access key is private. Only standard ss:// keys are supported.</p></>}
      {dialog === 'rename' && <><p>Choose a name to recognize this server.</p><label htmlFor="rename">Server name</label><input id="rename" value={name} onChange={e => setName(e.target.value)} maxLength={80} autoFocus/></>}
      {dialog === 'remove' && <p>Remove <strong>{target?.name}</strong> from this device? {active && target?.id === state.selectedProfileId ? 'Finley will disconnect first.' : 'You can add it again with its access key.'}</p>}
      {dialogError && <div className="dialog-error" role="alert">{dialogError}</div>}
      <div className="modal-actions"><button className="secondary" disabled={busy} onClick={() => setDialog(null)}>Cancel</button><button className={dialog === 'remove' ? 'danger' : 'primary compact'} disabled={busy || (dialog === 'add' && !key.trim()) || (dialog === 'rename' && !name.trim())} onClick={() => void saveDialog()}>{busy ? 'Please wait...' : dialog === 'add' ? 'Add server' : dialog === 'rename' ? 'Save name' : 'Remove server'}</button></div>
    </div></div>}
  </div>;
}

