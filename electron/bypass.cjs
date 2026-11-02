'use strict';
const path = require('node:path');
const net = require('node:net');
const { domainToASCII } = require('node:url');

function normalizeBypass(enabled, paths, domains = [], cidrs = []) {
 if (typeof enabled !== 'boolean' || !Array.isArray(paths) || !Array.isArray(domains) || !Array.isArray(cidrs) || paths.length > 200 || domains.length > 200 || cidrs.length > 200) throw new Error('Invalid bypass settings.');
 const appMap = new Map();
 for (const p of paths) {
  if (typeof p !== 'string' || p.length > 1024 || !path.win32.isAbsolute(p) || path.win32.extname(p).toLowerCase() !== '.exe') throw new Error('Select valid Windows executable files.');
  const normalized = path.win32.normalize(p); appMap.set(normalized.toLowerCase(), normalized);
 }
 const domainSet = new Set();
 for (const value of domains) {
  if (typeof value !== 'string' || value.length > 253) throw new Error('Enter a valid domain, such as example.com.');
  const input = value.trim().replace(/\.$/, '');
  if (!input || /[\/:*@?#\s]/u.test(input)) throw new Error('Enter a domain without a URL, port, or wildcard.');
  const ascii = domainToASCII(input).toLowerCase();
  const labels = ascii.split('.');
  if (!ascii || ascii.length > 253 || labels.length < 2 || net.isIP(ascii) || labels.some(label => !label || label.length > 63 || !/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(label))) throw new Error('Enter a valid domain, such as example.com.');
  domainSet.add(ascii);
 }
 const cidrSet = new Set();
 for (const value of cidrs) {
  if (typeof value !== 'string' || value.length > 64) throw new Error('Enter a valid IPv4 address or CIDR range.');
  const parts = value.trim().split('/');
  if (parts.length > 2 || net.isIP(parts[0]) !== 4 || (parts.length === 2 && !/^(?:0|[1-9]\d?)$/.test(parts[1]))) {
   if (parts[0].includes(':')) throw new Error('IPv6 bypass ranges are not available in this version.');
   throw new Error('Enter a valid IPv4 address or CIDR range.');
  }
  const bits = parts.length === 1 ? 32 : Number(parts[1]);
  if (bits > 32) throw new Error('IPv4 CIDR prefix must be between 0 and 32.');
  const octets = parts[0].split('.').map(Number); const ip = (((octets[0] << 24) | (octets[1] << 16) | (octets[2] << 8) | octets[3]) >>> 0);
  const mask = bits === 0 ? 0 : (0xffffffff << (32 - bits)) >>> 0; const start = (ip & mask) >>> 0;
  cidrSet.add(`${start >>> 24}.${(start >>> 16) & 255}.${(start >>> 8) & 255}.${start & 255}/${bits}`);
 }
 return {enabled,paths:[...appMap.values()],domains:[...domainSet],cidrs:[...cidrSet]};
}
module.exports = {normalizeBypass};
