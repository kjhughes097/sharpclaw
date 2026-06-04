import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import Grid from '@mui/material/Grid';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import CardActionArea from '@mui/material/CardActionArea';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import AddIcon from '@mui/icons-material/Add';
import { getTasks } from '../api/tasks';
import type { ScheduledTaskSummary } from '../api/tasks';

function formatDate(iso: string | null): string {
    if (!iso) return '—';
    return new Date(iso).toLocaleString();
}

export default function TaskListPage() {
    const [tasks, setTasks] = useState<ScheduledTaskSummary[]>([]);
    const navigate = useNavigate();

    useEffect(() => { getTasks().then(setTasks); }, []);

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
                <Typography variant="h4">Tasks</Typography>
                <Button variant="contained" startIcon={<AddIcon />} onClick={() => navigate('/tasks/new')}>
                    New Task
                </Button>
            </Box>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
                Scheduled tasks that run agents or commands on a cron schedule.
            </Typography>

            {tasks.length === 0 && (
                <Typography color="text.secondary">No scheduled tasks found.</Typography>
            )}

            <Grid container spacing={2}>
                {tasks.map((task) => (
                    <Grid key={task.id} size={{ xs: 12, sm: 6, md: 3 }}>
                        <Card variant="outlined" sx={{ height: '100%', opacity: task.enabled ? 1 : 0.6 }}>
                            <CardActionArea onClick={() => navigate(`/tasks/${task.id}`)} sx={{ height: '100%' }}>
                            <CardContent>
                                <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center' }}>
                                    <Typography variant="subtitle1" sx={{ fontWeight: 600, flex: 1 }} noWrap>
                                        {task.description || task.id}
                                    </Typography>
                                    <Chip
                                        label={task.enabled ? 'Active' : 'Disabled'}
                                        color={task.enabled ? 'success' : 'default'}
                                        size="small"
                                    />
                                </Stack>

                                <Typography variant="body2" color="text.secondary" sx={{ mb: 1, fontFamily: 'monospace' }}>
                                    {task.cron}
                                </Typography>

                                <Stack spacing={0.5} sx={{ mb: 1 }}>
                                    <Typography variant="caption" color="text.secondary">
                                        Type: <strong>{task.taskType === 'command' ? 'Command' : 'Agent'}</strong>
                                        {task.taskType !== 'command' && <> — {task.agent}</>}
                                    </Typography>
                                    <Typography variant="caption" color="text.secondary">
                                        Channel: {task.channelType}
                                    </Typography>
                                    {task.isOneOff && (
                                        <Chip label="One-off" size="small" variant="outlined" sx={{ width: 'fit-content' }} />
                                    )}
                                </Stack>

                                <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                                    Next run: {formatDate(task.nextRun)}
                                </Typography>
                                <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                                    Last run: {formatDate(task.lastRun)}
                                </Typography>

                                {task.prompt && (
                                    <Typography
                                        variant="body2"
                                        sx={{ mt: 1.5, fontSize: '0.75rem', color: 'text.secondary', whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}
                                    >
                                        {task.prompt}
                                    </Typography>
                                )}
                            </CardContent>
                            </CardActionArea>
                        </Card>
                    </Grid>
                ))}
            </Grid>
        </Box>
    );
}
