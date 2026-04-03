import { useState, type FormEvent } from 'react';
import clawIcon from './sharpclaw-pincer-detailed.svg';

interface Props {
  isConfigured: boolean;
  onSetup: (username: string, password: string, confirmPassword: string) => void;
  onLogin: (username: string, password: string) => void;
  error?: string;
}

export function LoginScreen({ isConfigured, onSetup, onLogin, error }: Props) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();

    if (!username.trim() || !password.trim())
      return;

    if (isConfigured) {
      onLogin(username.trim(), password);
      return;
    }

    if (confirmPassword.trim())
      onSetup(username.trim(), password, confirmPassword);
  };

  const canSubmit = isConfigured
    ? username.trim().length > 0 && password.trim().length > 0
    : username.trim().length > 0 && password.trim().length > 0 && confirmPassword.trim().length > 0;

  return (
    <div className="login-backdrop">
      <form className="login-card" onSubmit={handleSubmit}>
        <div className="login-logo" aria-hidden="true">
          <img className="brand-mark-image" src={clawIcon} alt="" />
        </div>
        <h1 className="login-title">SharpClaw</h1>
        <p className="login-subtitle">
          {isConfigured ? 'Sign in to continue' : 'Create your single admin login'}
        </p>
        {error && <div className="login-error">{error}</div>}
        <input
          className="login-input"
          type="text"
          placeholder="Username"
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          autoFocus
        />
        <input
          className="login-input"
          type="password"
          placeholder="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
        {!isConfigured && (
          <input
            className="login-input"
            type="password"
            placeholder="Confirm password"
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
          />
        )}
        <button className="login-btn" type="submit" disabled={!canSubmit}>
          {isConfigured ? 'Sign in' : 'Create login'}
        </button>
      </form>
    </div>
  );
}
