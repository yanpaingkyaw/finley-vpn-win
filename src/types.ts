export type Profile = { id: string; name: string; server: string; method: string };
export type AppState = {
  version: string;
  profiles: Profile[];
  selectedProfileId: string | null;
  status: 'disconnected' | 'connecting' | 'connected' | 'disconnecting' | 'error';
  error: string | null;
  bypass: { enabled: boolean; paths: string[]; domains: string[]; cidrs: string[] };
  theme: 'light' | 'dark';
};
export interface FinleyAPI {
  getState(): Promise<AppState>;
  addProfile(key: string, name: string): Promise<void>;
  renameProfile(id: string, name: string): Promise<void>;
  removeProfile(id: string): Promise<void>;
  selectProfile(id: string): Promise<void>;
  connect(id: string): Promise<void>;
  disconnect(): Promise<void>;
  setBypass(enabled: boolean, paths: string[], domains: string[], cidrs: string[]): Promise<void>;
  pickExecutable(): Promise<string | null>;
  setTheme(theme: 'light' | 'dark'): Promise<void>;
  onState(callback: (state: AppState) => void): () => void;
}
declare global { interface Window { finley: FinleyAPI } }
