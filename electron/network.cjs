'use strict';
const { createServiceClient } = require('./service-client.cjs');
const messages = {
 CLEANUP_UNCONFIRMED: 'VPN cleanup could not be confirmed. Check Windows network settings and ask an administrator to repair Finley.',
 ENGINE_EXIT: 'The VPN engine stopped unexpectedly. Traffic may use your regular connection.',
 CANCELLED: 'VPN connection was cancelled or timed out.',
 STARTUP_FAILED: 'Could not start or verify the VPN. Check your network and repair Finley if needed.'
};
function createNetworkController({ clientPath, onStatus = () => {}, clientFactory = createServiceClient } = {}) {
 let generation = 0, connecting = null;
 let status = { state: 'disconnected', engineReady: false, owned: false, cleanupConfirmed: true, error: null };
 const client = clientFactory({ clientPath,
  onStatus(value) {
   status = { ...value, state: value.state === 'busy' ? 'error' : value.state,
    error: value.state === 'busy' ? 'The VPN is in use by another app or Windows session.' : messages[value.errorCode] || (value.state === 'error' ? 'The Finley service reported a connection failure. Check your network and repair Finley if needed.' : null) };
   onStatus(status);
  },
  onFailure(error) {
   status = { ...status, state: 'error', engineReady: false, cleanupConfirmed: false, error: error.message };
   onStatus(status);
  }
 });
 return Object.freeze({
  async reconcile() { try { await client.request('getStatus'); } catch (error) { status = { ...status, state: 'error', engineReady: false, error: error.message }; onStatus(status); } return status; },
  async connect(profile, bypass) {
   if (connecting || status.owned) throw new Error('A VPN connection is already active or changing.');
   const command = ++generation;
   const operation = (async () => {
    await client.request('getStatus');
    if (command !== generation) throw new Error(messages.CANCELLED);
    await client.request('connect', { profile, bypass });
    if (command !== generation) throw new Error(messages.CANCELLED);
   })();
   connecting = operation;
   try { await operation; } finally { if (connecting === operation) connecting = null; }
  },
  async disconnect() {
   ++generation;
   if (!status.engineReady && !status.owned && !connecting) {
    if (!status.cleanupConfirmed) throw new Error(messages.CLEANUP_UNCONFIRMED);
    return status;
   }
   const result = await client.request('disconnect');
   if (!result.cleanupConfirmed) throw new Error(messages.CLEANUP_UNCONFIRMED);
   return result;
  },
  getStatus: () => status,
  close() { ++generation; client.close(); }
 });
}
module.exports = { createNetworkController };
