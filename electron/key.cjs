'use strict';
const METHODS = new Set(['aes-128-gcm','aes-192-gcm','aes-256-gcm','chacha20-ietf-poly1305','xchacha20-ietf-poly1305']);
function decode64(value) {
  if (!/^[A-Za-z0-9_+\/-]+={0,2}$/.test(value)) throw new Error('Invalid access key encoding.');
  const decoded = Buffer.from(value.replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8');
  if (!decoded || decoded.includes('\ufffd')) throw new Error('Invalid access key encoding.');
  return decoded;
}
function parseKey(input) {
  if (typeof input !== 'string' || input.length > 8192) throw new Error('Enter a valid access key.');
  const key = input.trim();
  if (!key.startsWith('ss://')) throw new Error('Only standard ss:// access keys are supported; dynamic keys are not supported.');
  try {
    let body = key.slice(5); let label = '';
    const hash = body.indexOf('#');
    if (hash !== -1) { label = decodeURIComponent(body.slice(hash + 1)); body = body.slice(0, hash); }
    if (body.includes('?')) throw new Error('Access key plugins and advanced options are not supported.');
    if (!body.includes('@')) body = decode64(body);
    const at = body.lastIndexOf('@');
    if (at < 1) throw new Error('Invalid access key.');
    let auth = body.slice(0, at); const endpoint = body.slice(at + 1).replace(/\/$/, '');
    if (!auth.includes(':')) auth = decode64(auth); else auth = decodeURIComponent(auth);
    const colon = auth.indexOf(':');
    if (colon < 1 || !auth.slice(colon + 1)) throw new Error('The access key is missing credentials.');
    const method = auth.slice(0, colon).toLowerCase();
    if (!METHODS.has(method)) throw new Error('This Shadowsocks cipher is not supported.');
    const url = new URL(`finley://${endpoint}`);
    if (url.username || url.password || url.pathname !== '' && url.pathname !== '/' || url.search || !url.port || !url.hostname) throw new Error('Invalid server address or port.');
    const port = Number(url.port);
    if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Invalid server address or port.');
    return { server: url.hostname, port, method, password: auth.slice(colon + 1), label: label.slice(0, 100) };
  } catch (e) {
    if (e instanceof TypeError || e instanceof URIError) throw new Error('Invalid access key or server address.');
    throw e;
  }
}
module.exports = { parseKey };

