import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Box from '@mui/material/Box';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Typography from '@mui/material/Typography';
import Grid from '@mui/material/Grid';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import Avatar from '@mui/material/Avatar';
import { LineChart } from '@mui/x-charts/LineChart';
import { BarChart } from '@mui/x-charts/BarChart';
import { SparkLineChart } from '@mui/x-charts/SparkLineChart';
import { getAgents } from '../api/agents';
import { getMcps } from '../api/mcps';
import { getTools } from '../api/tools';
import { getSkills } from '../api/skills';
import type { AgentSummary } from '../api/agents';

interface StatCardProps {
    title: string;
    value: string;
    trend: string;
    trendUp: boolean;
    data: number[];
}

function StatCard({ title, value, trend, trendUp, data }: StatCardProps) {
    return (
        <Card>
            <CardContent sx={{ pb: '16px !important' }}>
                <Typography variant="body2" color="text.secondary" gutterBottom>
                    {title}
                </Typography>
                <Stack direction="row" sx={{ alignItems: 'baseline', gap: 1 }}>
                    <Typography variant="h4" sx={{ fontWeight: 600 }}>
                        {value}
                    </Typography>
                    <Chip
                        label={trend}
                        size="small"
                        color={trendUp ? 'success' : 'error'}
                        sx={{ fontSize: '0.75rem', height: 20 }}
                    />
                </Stack>
                <Typography variant="caption" color="text.secondary">
                    Last 30 days
                </Typography>
                <Box sx={{ mt: 1, height: 40 }}>
                    <SparkLineChart
                        data={data}
                        height={40}
                        color={trendUp ? '#4caf50' : '#f44336'}
                    />
                </Box>
            </CardContent>
        </Card>
    );
}

// Mock session data for the line chart
const sessionDays = ['Week 1', 'Week 2', 'Week 3', 'Week 4'];
const sessionData = [42, 67, 89, 112];

// Mock monthly data for bar chart
const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun'];
const invocationsData = [320, 480, 620, 750, 890, 1020];

export default function HomePage() {
    const [agents, setAgents] = useState<AgentSummary[]>([]);
    const [mcpCount, setMcpCount] = useState(0);
    const [toolCount, setToolCount] = useState(0);
    const [skillCount, setSkillCount] = useState(0);
    const navigate = useNavigate();

    useEffect(() => {
        getAgents().then(setAgents);
        getMcps().then((m) => setMcpCount(m.length));
        getTools().then((t) => setToolCount(t.length));
        getSkills().then((s) => setSkillCount(s.length));
    }, []);

    return (
        <Box>
            <Typography variant="h5" sx={{ fontWeight: 600, mb: 2 }}>
                Overview
            </Typography>

            {/* Agent Cards */}
            <Grid container spacing={2} sx={{ mb: 3 }} columns={10}>
                {agents.map((agent) => (
                    <Grid key={agent.name} size={{ xs: 5, sm: 2 }}>
                        <Card
                            sx={{ cursor: 'pointer' }}
                            onClick={() => navigate(`/agents/${agent.name}`)}
                        >
                            <CardContent sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 1, py: 2, px: 1, '&:last-child': { pb: 2 } }}>
                                <Avatar
                                    src={`https://api.dicebear.com/7.x/bottts/svg?seed=${agent.name}`}
                                    alt={agent.name}
                                    sx={{ width: 48, height: 48 }}
                                />
                                <Typography variant="body2" sx={{ fontWeight: 600, textAlign: 'center' }}>
                                    {agent.name.charAt(0).toUpperCase() + agent.name.slice(1)}
                                </Typography>
                                <Stack direction="row" sx={{ flexWrap: 'wrap', justifyContent: 'center', gap: 0.5 }}>
                                    {agent.llm && <Chip label={agent.llm} size="small" color="primary" sx={{ fontSize: '0.65rem', height: 18 }} />}
                                    {agent.model && <Chip label={agent.model.split('/').pop()!.split('-').slice(0, 2).join('-')} size="small" sx={{ fontSize: '0.65rem', height: 18 }} />}
                                    {agent.toolNames && <Chip label={`${agent.toolNames.length} tools`} size="small" variant="outlined" sx={{ fontSize: '0.65rem', height: 18 }} />}
                                </Stack>
                            </CardContent>
                        </Card>
                    </Grid>
                ))}
            </Grid>

            {/* Stat Cards */}
            <Grid container spacing={2} sx={{ mb: 3 }}>
                <Grid size={{ xs: 12, sm: 6, md: 3 }}>
                    <StatCard title="Agents" value={String(agents.length)} trend="+2" trendUp data={[3, 3, 4, 4, 5, 5, 5]} />
                </Grid>
                <Grid size={{ xs: 12, sm: 6, md: 3 }}>
                    <StatCard title="MCP Servers" value={String(mcpCount)} trend="+1" trendUp data={[2, 2, 3, 3, 3, 4, 4]} />
                </Grid>
                <Grid size={{ xs: 12, sm: 6, md: 3 }}>
                    <StatCard title="Tools" value={String(toolCount)} trend="+5" trendUp data={[8, 9, 10, 12, 14, 15, 17]} />
                </Grid>
                <Grid size={{ xs: 12, sm: 6, md: 3 }}>
                    <StatCard title="Skills" value={String(skillCount)} trend="0" trendUp={false} data={[4, 4, 4, 4, 4, 4, 4]} />
                </Grid>
            </Grid>

            {/* Charts Row */}
            <Grid container spacing={2} sx={{ mb: 3 }}>
                <Grid size={{ xs: 12, md: 7 }}>
                    <Card>
                        <CardContent>
                            <Typography variant="subtitle1" sx={{ fontWeight: 600, mb: 1 }}>
                                Sessions
                            </Typography>
                            <Typography variant="caption" color="text.secondary">
                                Agent invocations per week
                            </Typography>
                            <LineChart
                                height={220}
                                series={[{ data: sessionData, label: 'Sessions', color: '#1976d2' }]}
                                xAxis={[{ scaleType: 'point', data: sessionDays }]}
                                sx={{ '& .MuiChartsLegend-root': { display: 'none' } }}
                            />
                        </CardContent>
                    </Card>
                </Grid>
                <Grid size={{ xs: 12, md: 5 }}>
                    <Card>
                        <CardContent>
                            <Typography variant="subtitle1" sx={{ fontWeight: 600, mb: 1 }}>
                                Tool Invocations
                            </Typography>
                            <Typography variant="caption" color="text.secondary">
                                Monthly tool calls across all agents
                            </Typography>
                            <BarChart
                                height={220}
                                series={[{ data: invocationsData, label: 'Invocations', color: '#42a5f5' }]}
                                xAxis={[{ scaleType: 'band', data: months }]}
                                sx={{ '& .MuiChartsLegend-root': { display: 'none' } }}
                            />
                        </CardContent>
                    </Card>
                </Grid>
            </Grid>
        </Box>
    );
}
