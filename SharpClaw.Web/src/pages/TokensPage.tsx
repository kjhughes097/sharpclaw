import { useEffect, useState } from 'react';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import Paper from '@mui/material/Paper';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Stack from '@mui/material/Stack';
import Chip from '@mui/material/Chip';
import TextField from '@mui/material/TextField';
import MenuItem from '@mui/material/MenuItem';
import { getTokenSummary, getTokenDaily, getTokenRecent } from '../api/tokens';
import type { TokenUsageSummary, TokenUsageDaily, TokenUsageEntry } from '../api/tokens';

function formatNumber(n: number): string {
    if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
    if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
    return n.toString();
}

function formatDuration(ms: number | null): string {
    if (ms === null) return '—';
    if (ms >= 1000) return `${(ms / 1000).toFixed(1)}s`;
    return `${Math.round(ms)}ms`;
}

export default function TokensPage() {
    const [summary, setSummary] = useState<TokenUsageSummary[]>([]);
    const [daily, setDaily] = useState<TokenUsageDaily[]>([]);
    const [recent, setRecent] = useState<TokenUsageEntry[]>([]);
    const [filterAgent, setFilterAgent] = useState('');
    const [filterProvider, setFilterProvider] = useState('');

    const loadData = () => {
        const params = {
            agent: filterAgent || undefined,
            provider: filterProvider || undefined,
        };
        getTokenSummary(params).then(setSummary);
        getTokenDaily(params).then(setDaily);
        getTokenRecent({ ...params, limit: 100 }).then(setRecent);
    };

    useEffect(() => { loadData(); }, [filterAgent, filterProvider]);

    const totalInput = summary.reduce((acc, s) => acc + s.totalInputTokens, 0);
    const totalOutput = summary.reduce((acc, s) => acc + s.totalOutputTokens, 0);
    const totalRequests = summary.reduce((acc, s) => acc + s.requestCount, 0);

    const agents = [...new Set(summary.map(s => s.agentName))];
    const providers = [...new Set(summary.map(s => s.provider))];

    // Daily chart as a simple bar visualization
    const maxDailyTokens = Math.max(...daily.map(d => d.totalInputTokens + d.totalOutputTokens), 1);

    return (
        <Box>
            <Typography variant="h4" sx={{ mb: 1 }}>Tokens</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
                Token usage across all LLM interactions.
            </Typography>

            {/* Filters */}
            <Stack direction="row" spacing={2} sx={{ mb: 3 }}>
                <TextField
                    select
                    label="Agent"
                    value={filterAgent}
                    onChange={(e) => setFilterAgent(e.target.value)}
                    size="small"
                    sx={{ minWidth: 150 }}
                >
                    <MenuItem value="">All</MenuItem>
                    {agents.map(a => <MenuItem key={a} value={a}>{a}</MenuItem>)}
                </TextField>
                <TextField
                    select
                    label="Provider"
                    value={filterProvider}
                    onChange={(e) => setFilterProvider(e.target.value)}
                    size="small"
                    sx={{ minWidth: 150 }}
                >
                    <MenuItem value="">All</MenuItem>
                    {providers.map(p => <MenuItem key={p} value={p}>{p}</MenuItem>)}
                </TextField>
            </Stack>

            {/* Summary Cards */}
            <Stack direction="row" spacing={2} sx={{ mb: 3 }}>
                <Paper variant="outlined" sx={{ p: 2, flex: 1, textAlign: 'center' }}>
                    <Typography variant="h5" color="primary">{formatNumber(totalInput + totalOutput)}</Typography>
                    <Typography variant="caption" color="text.secondary">Total Tokens</Typography>
                </Paper>
                <Paper variant="outlined" sx={{ p: 2, flex: 1, textAlign: 'center' }}>
                    <Typography variant="h5" color="info.main">{formatNumber(totalInput)}</Typography>
                    <Typography variant="caption" color="text.secondary">Input Tokens</Typography>
                </Paper>
                <Paper variant="outlined" sx={{ p: 2, flex: 1, textAlign: 'center' }}>
                    <Typography variant="h5" color="success.main">{formatNumber(totalOutput)}</Typography>
                    <Typography variant="caption" color="text.secondary">Output Tokens</Typography>
                </Paper>
                <Paper variant="outlined" sx={{ p: 2, flex: 1, textAlign: 'center' }}>
                    <Typography variant="h5">{totalRequests}</Typography>
                    <Typography variant="caption" color="text.secondary">Requests</Typography>
                </Paper>
            </Stack>

            {/* Daily Usage Chart */}
            {daily.length > 0 && (
                <Paper variant="outlined" sx={{ p: 2, mb: 3 }}>
                    <Typography variant="subtitle2" sx={{ mb: 1.5 }}>Daily Usage (last {daily.length} days)</Typography>
                    <Stack spacing={0.5}>
                        {daily.slice().reverse().map((d) => {
                            const total = d.totalInputTokens + d.totalOutputTokens;
                            return (
                                <Stack key={d.date} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                                    <Typography variant="caption" sx={{ minWidth: 80, fontFamily: 'monospace' }}>
                                        {d.date}
                                    </Typography>
                                    <Box sx={{ flex: 1, position: 'relative', height: 20 }}>
                                        <Box sx={{
                                            position: 'absolute',
                                            left: 0,
                                            top: 0,
                                            height: '100%',
                                            width: `${(d.totalInputTokens / maxDailyTokens) * 100}%`,
                                            bgcolor: 'info.main',
                                            opacity: 0.7,
                                            borderRadius: 0.5,
                                        }} />
                                        <Box sx={{
                                            position: 'absolute',
                                            left: `${(d.totalInputTokens / maxDailyTokens) * 100}%`,
                                            top: 0,
                                            height: '100%',
                                            width: `${(d.totalOutputTokens / maxDailyTokens) * 100}%`,
                                            bgcolor: 'success.main',
                                            opacity: 0.7,
                                            borderRadius: 0.5,
                                        }} />
                                    </Box>
                                    <Typography variant="caption" sx={{ minWidth: 60, textAlign: 'right', fontFamily: 'monospace' }}>
                                        {formatNumber(total)}
                                    </Typography>
                                </Stack>
                            );
                        })}
                    </Stack>
                    <Stack direction="row" spacing={2} sx={{ mt: 1 }}>
                        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                            <Box sx={{ width: 12, height: 12, bgcolor: 'info.main', opacity: 0.7, borderRadius: 0.5 }} />
                            <Typography variant="caption">Input</Typography>
                        </Stack>
                        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                            <Box sx={{ width: 12, height: 12, bgcolor: 'success.main', opacity: 0.7, borderRadius: 0.5 }} />
                            <Typography variant="caption">Output</Typography>
                        </Stack>
                    </Stack>
                </Paper>
            )}

            {/* Summary by Agent/Model */}
            {summary.length > 0 && (
                <Paper variant="outlined" sx={{ mb: 3 }}>
                    <Typography variant="subtitle2" sx={{ p: 2, pb: 0 }}>Usage by Agent & Model</Typography>
                    <TableContainer>
                        <Table size="small">
                            <TableHead>
                                <TableRow>
                                    <TableCell>Agent</TableCell>
                                    <TableCell>Provider</TableCell>
                                    <TableCell>Model</TableCell>
                                    <TableCell align="right">Requests</TableCell>
                                    <TableCell align="right">Input</TableCell>
                                    <TableCell align="right">Output</TableCell>
                                    <TableCell align="right">Avg Duration</TableCell>
                                </TableRow>
                            </TableHead>
                            <TableBody>
                                {summary.map((s, i) => (
                                    <TableRow key={i}>
                                        <TableCell>{s.agentName}</TableCell>
                                        <TableCell>
                                            <Chip label={s.provider} size="small" variant="outlined" />
                                        </TableCell>
                                        <TableCell>
                                            <Typography variant="caption" sx={{ fontFamily: 'monospace' }}>
                                                {s.model ?? '—'}
                                            </Typography>
                                        </TableCell>
                                        <TableCell align="right">{s.requestCount}</TableCell>
                                        <TableCell align="right">{formatNumber(s.totalInputTokens)}</TableCell>
                                        <TableCell align="right">{formatNumber(s.totalOutputTokens)}</TableCell>
                                        <TableCell align="right">{formatDuration(s.avgDurationMs)}</TableCell>
                                    </TableRow>
                                ))}
                            </TableBody>
                        </Table>
                    </TableContainer>
                </Paper>
            )}

            {/* Recent Interactions */}
            <Paper variant="outlined">
                <Typography variant="subtitle2" sx={{ p: 2, pb: 0 }}>Recent Interactions</Typography>
                <TableContainer>
                    <Table size="small">
                        <TableHead>
                            <TableRow>
                                <TableCell>Time</TableCell>
                                <TableCell>Agent</TableCell>
                                <TableCell>Provider</TableCell>
                                <TableCell>Model</TableCell>
                                <TableCell align="right">Input</TableCell>
                                <TableCell align="right">Output</TableCell>
                                <TableCell align="right">Duration</TableCell>
                                <TableCell align="center">Status</TableCell>
                            </TableRow>
                        </TableHead>
                        <TableBody>
                            {recent.map((r) => (
                                <TableRow key={r.id}>
                                    <TableCell>
                                        <Typography variant="caption" sx={{ fontFamily: 'monospace' }}>
                                            {new Date(r.timestampUtc).toLocaleString()}
                                        </Typography>
                                    </TableCell>
                                    <TableCell>{r.agentName}</TableCell>
                                    <TableCell>
                                        <Chip label={r.provider} size="small" variant="outlined" />
                                    </TableCell>
                                    <TableCell>
                                        <Typography variant="caption" sx={{ fontFamily: 'monospace' }}>
                                            {r.model ?? '—'}
                                        </Typography>
                                    </TableCell>
                                    <TableCell align="right">
                                        {r.inputTokens !== null ? formatNumber(r.inputTokens) : '—'}
                                    </TableCell>
                                    <TableCell align="right">
                                        {r.outputTokens !== null ? formatNumber(r.outputTokens) : '—'}
                                    </TableCell>
                                    <TableCell align="right">{formatDuration(r.durationMs)}</TableCell>
                                    <TableCell align="center">
                                        <Chip
                                            label={r.success ? 'OK' : 'Error'}
                                            size="small"
                                            color={r.success ? 'success' : 'error'}
                                            variant="outlined"
                                        />
                                    </TableCell>
                                </TableRow>
                            ))}
                            {recent.length === 0 && (
                                <TableRow>
                                    <TableCell colSpan={8} align="center">
                                        <Typography variant="body2" color="text.secondary" sx={{ py: 3 }}>
                                            No token usage recorded yet.
                                        </Typography>
                                    </TableCell>
                                </TableRow>
                            )}
                        </TableBody>
                    </Table>
                </TableContainer>
            </Paper>
        </Box>
    );
}
