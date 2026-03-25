import { useState, type FormEvent } from 'react';
import clawIcon from './sharpclaw-pincer-detailed.svg';

interface Props {
  onLogin: (key: string) => void;
  error?: string;
}

export function LoginScreen({ onLogin, error }: Props) {
  const [key, setKey] = useState('');

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    if (key.trim()) onLogin(key.trim());
  };

  return (
    <div className="login-backdrop">
      <form className="login-card" onSubmit={handleSubmit}>
        <div className="login-logo" aria-hidden="true">
          <img className="brand-mark-image" src={clawIcon} alt="" />
        </div>
        <h1 className="login-title">SharpClaw</h1>
        <p className="login-subtitle">Enter your API key to continue</p>
        {error && <div className="login-error">{error}</div>}
        <input
          className="login-input"
          type="password"
          placeholder="API Key"
          value={key}
          onChange={(e) => setKey(e.target.value)}
          autoFocus
        />
        <button className="login-btn" type="submit" disabled={!key.trim()}>
          Sign in
        </button>
      </form>
    </div>
  );
}
