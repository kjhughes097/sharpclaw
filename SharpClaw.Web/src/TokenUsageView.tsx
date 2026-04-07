import { useEffect, useMemo, useState } from 'react';
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer,
} from 'recharts';
import { fetchTokenUsageSummary, fetchTokenUsageHistory } from './api';
import type { TokenUsageSummary, TokenUsageHistory } from './types';

interface TokenUsageViewProps {
  onMenuClick: () => void;
}

const AGENT_COLORS = [
  '#0f6cbd', '#e03e3e', '#2b9a3e', '#d97706', '#7c3aed',
  '#0891b2', '#db2777', '#65a30d', '#6366f1', '#ea580c',
];

function formatTokenCount(count: number): string {
  if (count >= 1_000_000) return `${(count / 1_000_000).toFixed(1)}M`;
  if (count >= 1_000) return `${(count / 1_000).toFixed(count >= 10_000 ? 0 : 1)}k`;
  return `${count}`;
}

export function TokenUsageView({ onMenuClick }: TokenUsageViewProps) {
  const [summary, setSummary] = useState<TokenUsageSummary | null>(null);
  const [history, setHistory] = useState<TokenUsageHistory | null>(null);
  const [period, setPeriod] = useState<'day' | 'week' | 'month'>('week');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void loadData();
  }, [period]);

  async function loadData() {
    setLoading(true);
    setError(null);

    try {
      const [summaryResult, historyResult] = await Promise.all([
        fetchTokenUsageSummary(),
        fetchTokenUsageHistory(period),
      ]);

      setSummary(summaryResult);
      setHistory(historyResult);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  const { chartData, agents } = useMemo(() => {
    if (!history) return { chartData: [], agents: [] };

    const agentSet = new Set<string>();
    const bucketMap = new Map<string, Record<string, number>>();

    for (const dp of history.dataPoints) {
      agentSet.add(dp.agentSlug);
      const existing = bucketMap.get(dp.bucket) ?? {};
      existing[dp.agentSlug] = (existing[dp.agentSlug] ?? 0) + dp.totalTokens;
      bucketMap.set(dp.bucket, existing);
    }

    const agents = [...agentSet].sort();
    const chartData = [...bucketMap.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([bucket, data]) => ({ bucket, ...data }));

    return { chartData, agents };
  }, [history]);

  return (
    <div className="chat-area">
      <div className="chat-header">
        <button className="menu-btn" onClick={onMenuClick} aria-label="Open menu">☰</button>
        <div className="config-header-copy">
          <strong>Token Usage</strong>
          <span>Monitor daily token usage across providers and agents.</span>
        </div>
      </div>

      {error && <div className="config-banner error">{error}</div>}

      {loading ? (
        <div className="config-empty-state">Loading token usage data…</div>
      ) : (
        <div className="token-usage-content">
          {/* ── Provider Usage Summary ── */}
          {summary && summary.providers.length > 0 && (
            <section className="token-usage-section">
              <h3>Provider Usage Today</h3>
              <div className="token-usage-cards">
                {summary.providers.map(p => {
                  const level = p.usagePercent >= 90 ? 'danger' : p.usagePercent >= 75 ? 'warning' : 'normal';
                  return (
                    <div key={p.provider} className={`token-usage-card ${level}`}>
                      <div className="token-usage-card-header">
                        <strong>{p.provider}</strong>
                        <span className={`token-usage-badge ${level}`}>
                          {p.usagePercent.toFixed(1)}%
                        </span>
                      </div>
                      <div className="token-usage-bar-track">
                        <div
                          className={`token-usage-bar-fill ${level}`}
                          style={{ width: `${Math.min(100, p.usagePercent)}%` }}
                        />
                      </div>
                      <div className="token-usage-card-stats">
                        <span>{formatTokenCount(p.totalTokens)} / {formatTokenCount(p.dailyLimit)} tokens</span>
                      </div>
                    </div>
                  );
                })}
              </div>
            </section>
          )}

          {/* ── Agent Usage Summary ── */}
          {summary && summary.agents.filter(a => a.dailyLimit !== null || a.totalTokens > 0).length > 0 && (
            <section className="token-usage-section">
              <h3>Agent Usage Today</h3>
              <div className="token-usage-cards">
                {summary.agents
                  .filter(a => a.dailyLimit !== null || a.totalTokens > 0)
                  .map(a => {
                    const level = a.usagePercent !== null
                      ? (a.usagePercent >= 90 ? 'danger' : a.usagePercent >= 75 ? 'warning' : 'normal')
                      : 'normal';
                    return (
                      <div key={a.agentSlug} className={`token-usage-card ${level}`}>
                        <div className="token-usage-card-header">
                          <strong>{a.agentSlug}</strong>
                          {a.usagePercent !== null && (
                            <span className={`token-usage-badge ${level}`}>
                              {a.usagePercent.toFixed(1)}%
                            </span>
                          )}
                        </div>
                        {a.dailyLimit !== null && (
                          <div className="token-usage-bar-track">
                            <div
                              className={`token-usage-bar-fill ${level}`}
                              style={{ width: `${Math.min(100, a.usagePercent ?? 0)}%` }}
                            />
                          </div>
                        )}
                        <div className="token-usage-card-stats">
                          <span>
                            {formatTokenCount(a.totalTokens)}
                            {a.dailyLimit !== null ? ` / ${formatTokenCount(a.dailyLimit)} tokens` : ' tokens'}
                          </span>
                        </div>
                      </div>
                    );
                  })}
              </div>
            </section>
          )}

          {/* ── Chart ── */}
          <section className="token-usage-section">
            <div className="token-usage-chart-header">
              <h3>Token Usage History</h3>
              <div className="token-usage-period-tabs">
                {(['day', 'week', 'month'] as const).map(p => (
                  <button
                    key={p}
                    className={`token-usage-period-tab ${period === p ? 'active' : ''}`}
                    onClick={() => setPeriod(p)}
                  >
                    {p === 'day' ? 'Last 24h' : p === 'week' ? 'Past Week' : 'Past Month'}
                  </button>
                ))}
              </div>
            </div>

            {chartData.length === 0 ? (
              <div className="config-empty-state">No usage data available for this period.</div>
            ) : (
              <div className="token-usage-chart-container">
                <ResponsiveContainer width="100%" height={360}>
                  <BarChart data={chartData} margin={{ top: 8, right: 24, left: 8, bottom: 8 }}>
                    <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
                    <XAxis
                      dataKey="bucket"
                      tick={{ fill: 'var(--text-dim)', fontSize: 12 }}
                      tickLine={false}
                    />
                    <YAxis
                      tickFormatter={formatTokenCount}
                      tick={{ fill: 'var(--text-dim)', fontSize: 12 }}
                      tickLine={false}
                    />
                    <Tooltip
                      formatter={(value) => [formatTokenCount(Number(value ?? 0)), '']}
                      contentStyle={{
                        background: 'var(--bg-input)',
                        border: '1px solid var(--border)',
                        borderRadius: '8px',
                        color: 'var(--text)',
                      }}
                    />
                    <Legend />
                    {agents.map((agent, idx) => (
                      <Bar
                        key={agent}
                        dataKey={agent}
                        name={agent}
                        stackId="tokens"
                        fill={AGENT_COLORS[idx % AGENT_COLORS.length]}
                        radius={idx === agents.length - 1 ? [4, 4, 0, 0] : [0, 0, 0, 0]}
                      />
                    ))}
                  </BarChart>
                </ResponsiveContainer>
              </div>
            )}
          </section>
        </div>
      )}
    </div>
  );
}
