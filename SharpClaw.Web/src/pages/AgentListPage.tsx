import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Typography from '@mui/material/Typography';
import Avatar from '@mui/material/Avatar';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import Grid from '@mui/material/Grid';
import AddIcon from '@mui/icons-material/Add';
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

export default function AgentListPage() {
    const [agents, setAgents] = useState<AgentActivity[]>([]);
    const navigate = useNavigate();

    useEffect(() => {
        apiFetch<AgentActivity[]>('/agents/activity').then(setAgents);
    }, []);

    const yMax = Math.max(1, ...agents.flatMap((a) => a.activity.map((d) => d.turns)));

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
                <Typography variant="h4" sx={{ fontWeight: 600 }}>Agents</Typography>
                <Button variant="contained" startIcon={<AddIcon />} onClick={() => navigate('/agents/new')}>
                    New Agent
                </Button>
            </Box>
            <Stack spacing={2}>
                {agents.map((agent) => (
                    <Card
                        key={agent.name}
                        sx={{ cursor: 'pointer', '&:hover': { boxShadow: 3 } }}
                        onClick={() => navigate(`/agents/${agent.name}`)}
                    >
                        <CardContent sx={{ py: 2, '&:last-child': { pb: 2 } }}>
                            <Grid container spacing={2} sx={{ alignItems: 'center' }}>
                                {/* Agent Card (1/4) */}
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
                                {/* Activity Chart (1/2) */}
                                <Grid size={{ xs: 12, md: 5 }}>
                                    <LineChart
                                        height={140}
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
                                        sx={{
                                            '& .MuiChartsLegend-root': { display: 'none' },
                                        }}
                                    />
                                </Grid>
                                {/* Tools & Skills (1/4) */}
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
                ))}
            </Stack>
        </Box>
    );
}
