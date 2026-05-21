import { useEffect, useRef, useState } from 'react';
import { useParams } from 'react-router-dom';
import Box from '@mui/material/Box';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Typography from '@mui/material/Typography';
import Avatar from '@mui/material/Avatar';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import Grid from '@mui/material/Grid';
import TextField from '@mui/material/TextField';
import IconButton from '@mui/material/IconButton';
import CircularProgress from '@mui/material/CircularProgress';
import SendRoundedIcon from '@mui/icons-material/SendRounded';
import Paper from '@mui/material/Paper';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { LineChart } from '@mui/x-charts/LineChart';
import { apiFetch } from '../api/client';

interface AgentActivity {
    name: string;
    description: string | null;
    llm: string | null;
    model: string | null;
    toolNames: string[];
    skillNames: string[];
    activity: { date: string; turns: number }[];
}

interface ChatMessage {
    turnType: string;
    content: string;
    timestamp: string;
}

export default function AgentChatPage() {
    const { name } = useParams<{ name: string }>();
    const [agent, setAgent] = useState<AgentActivity | null>(null);
    const [messages, setMessages] = useState<ChatMessage[]>([]);
    const [input, setInput] = useState('');
    const [sending, setSending] = useState(false);
    const messagesEndRef = useRef<HTMLDivElement>(null);

    useEffect(() => {
        if (!name) return;
        // Load agent info
        apiFetch<AgentActivity[]>('/agents/activity').then((agents) => {
            const found = agents.find((a) => a.name === name);
            if (found) setAgent(found);
        });
        // Load last 10 turns
        apiFetch<ChatMessage[]>(`/chat/${name}/history?limit=20`).then(setMessages);
    }, [name]);

    useEffect(() => {
        messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
    }, [messages]);

    const handleSend = async () => {
        if (!input.trim() || !name || sending) return;
        const text = input.trim();
        setInput('');
        setSending(true);

        // Optimistically add user message
        const userMsg: ChatMessage = { turnType: 'request', content: text, timestamp: new Date().toISOString() };
        setMessages((prev) => [...prev, userMsg]);

        try {
            const res = await apiFetch<{ response: string | null; switchedTo: string | null }>(`/chat/${name}`, {
                method: 'POST',
                body: JSON.stringify({ text }),
            });
            if (res.response) {
                const agentMsg: ChatMessage = { turnType: 'response', content: res.response, timestamp: new Date().toISOString() };
                setMessages((prev) => [...prev, agentMsg]);
            }
        } catch (err) {
            const errorMsg: ChatMessage = { turnType: 'response', content: `_Error: ${err instanceof Error ? err.message : 'Unknown error'}_`, timestamp: new Date().toISOString() };
            setMessages((prev) => [...prev, errorMsg]);
        } finally {
            setSending(false);
        }
    };

    const handleKeyDown = (e: React.KeyboardEvent) => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            handleSend();
        }
    };

    if (!name) return null;

    const yMax = agent ? Math.max(1, ...agent.activity.map((d) => d.turns)) : 1;

    return (
        <Box sx={{ display: 'flex', flexDirection: 'column', height: 'calc(100vh - 100px)', overflow: 'hidden' }}>
            {/* Agent Info Row */}
            {agent && (
                <Card sx={{ mb: 2, flexShrink: 0, bgcolor: 'primary.main', color: 'primary.contrastText', '& .MuiTypography-caption': { color: 'primary.contrastText', opacity: 0.7 }, '& .MuiChip-root': { borderColor: 'rgba(255,255,255,0.5)', color: 'primary.contrastText' } }}>
                    <CardContent sx={{ py: 2, '&:last-child': { pb: 2 } }}>
                        <Grid container spacing={2} sx={{ alignItems: 'center' }}>
                            <Grid size={{ xs: 12, md: 3 }}>
                                <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
                                    <Avatar
                                        src={`https://api.dicebear.com/7.x/bottts/svg?seed=${agent.name}`}
                                        alt={agent.name}
                                        sx={{ width: 48, height: 48 }}
                                    />
                                    <Box>
                                        <Typography variant="h5" sx={{ fontWeight: 600 }}>
                                            {agent.name.charAt(0).toUpperCase() + agent.name.slice(1)}
                                        </Typography>
                                        {agent.description && (
                                            <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                                                {agent.description}
                                            </Typography>
                                        )}
                                        <Stack direction="row" spacing={0.5} sx={{ mt: 0.5 }}>
                                            {agent.llm && <Chip label={agent.llm} size="small" color="primary" sx={{ fontSize: '0.65rem', height: 18 }} />}
                                            {agent.model && <Chip label={agent.model.split('-').slice(0, 2).join('-')} size="small" sx={{ fontSize: '0.65rem', height: 18 }} />}
                                            <Chip label={`${agent.toolNames.length} tools`} size="small" variant="outlined" sx={{ fontSize: '0.65rem', height: 18 }} />
                                        </Stack>
                                    </Box>
                                </Stack>
                            </Grid>
                            <Grid size={{ xs: 12, md: 5 }}>
                                <LineChart
                                    height={100}
                                    series={[{
                                        data: agent.activity.map((d) => d.turns),
                                        color: '#42a5f5',
                                        showMark: false,
                                    }]}
                                    xAxis={[{
                                        scaleType: 'point',
                                        data: agent.activity.map((d) => d.date.slice(5)),
                                        tickLabelStyle: { display: 'none' },
                                        tickSize: 0,
                                    }]}
                                    yAxis={[{ min: 0, max: yMax }]}
                                    margin={{ top: 10, bottom: 10, left: 30, right: 10 }}
                                    sx={{ '& .MuiChartsLegend-root': { display: 'none' }, '& .MuiChartsAxis-tickLabel': { fill: '#fff' }, '& .MuiChartsAxis-line': { stroke: '#fff' }, '& .MuiChartsAxis-tick': { stroke: '#fff' } }}
                                />
                            </Grid>
                            <Grid size={{ xs: 12, md: 4 }}>
                                <Box>
                                    {agent.toolNames.length > 0 && (
                                        <Box sx={{ mb: 1 }}>
                                            <Typography variant="caption" sx={{ display: 'block', mb: 0.5, fontWeight: 700, color: 'primary.main' }}>
                                                Tools
                                            </Typography>
                                            <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.5 }}>
                                                {agent.toolNames.map((t) => (
                                                    <Chip key={t} label={t} size="small" variant="outlined" sx={{ fontSize: '0.65rem', height: 20 }} />
                                                ))}
                                            </Box>
                                        </Box>
                                    )}
                                    {agent.skillNames.length > 0 && (
                                        <Box>
                                            <Typography variant="caption" sx={{ display: 'block', mb: 0.5, fontWeight: 700, color: 'secondary.main' }}>
                                                Skills
                                            </Typography>
                                            <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.5 }}>
                                                {agent.skillNames.map((s) => (
                                                    <Chip key={s} label={s} size="small" color="secondary" variant="outlined" sx={{ fontSize: '0.65rem', height: 20 }} />
                                                ))}
                                            </Box>
                                        </Box>
                                    )}
                                </Box>
                            </Grid>
                        </Grid>
                    </CardContent>
                </Card>
            )}

            {/* Chat Container */}
            <Card variant="outlined" sx={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden', minHeight: 0 }}>
                <CardContent sx={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden', minHeight: 0 }}>
                    {/* Chat Messages */}
                    <Box sx={{ flex: 1, overflow: 'auto', mb: 2, minHeight: 0 }}>
                        <Stack spacing={1.5}>
                            {messages.map((msg, i) => (
                                <Box
                                    key={i}
                                    sx={{
                                        display: 'flex',
                                        justifyContent: msg.turnType === 'request' ? 'flex-end' : 'flex-start',
                                    }}
                                >
                                    <Paper
                                        elevation={1}
                                        sx={{
                                            px: 2,
                                            py: 1,
                                            maxWidth: '75%',
                                            fontSize: '0.875rem',
                                            bgcolor: msg.turnType === 'request' ? 'primary.main' : 'grey.100',
                                            color: msg.turnType === 'request' ? 'primary.contrastText' : 'text.primary',
                                            borderRadius: 2,
                                            '& p': { m: 0 },
                                            '& pre': { overflow: 'auto', bgcolor: 'grey.900', color: 'grey.100', p: 1, borderRadius: 1, fontSize: '0.75rem' },
                                            '& code': { fontSize: '0.75rem' },
                                        }}
                                    >
                                        <Markdown remarkPlugins={[remarkGfm]}>{msg.content}</Markdown>
                                    </Paper>
                                </Box>
                            ))}
                            <div ref={messagesEndRef} />
                        </Stack>
                    </Box>

                    {/* Input */}
                    <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-end' }}>
                        <TextField
                            fullWidth
                            multiline
                            maxRows={4}
                            placeholder="Send a message..."
                            value={input}
                            onChange={(e) => setInput(e.target.value)}
                            onKeyDown={handleKeyDown}
                            disabled={sending}
                            size="small"
                            sx={{ '& .MuiOutlinedInput-root': { borderRadius: 2 } }}
                        />
                        <IconButton
                            color="primary"
                            onClick={handleSend}
                            disabled={!input.trim() || sending}
                        >
                            {sending ? <CircularProgress size={24} /> : <SendRoundedIcon />}
                        </IconButton>
                    </Stack>
                </CardContent>
            </Card>
        </Box>
    );
}
