import { useEffect, useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Switch from '@mui/material/Switch';
import FormControlLabel from '@mui/material/FormControlLabel';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import Dialog from '@mui/material/Dialog';
import DialogTitle from '@mui/material/DialogTitle';
import DialogActions from '@mui/material/DialogActions';
import Alert from '@mui/material/Alert';
import Chip from '@mui/material/Chip';
import SaveIcon from '@mui/icons-material/Save';
import DeleteIcon from '@mui/icons-material/Delete';
import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import Editor from '@monaco-editor/react';
import { getTask, updateTask, deleteTask } from '../api/tasks';
import type { ScheduledTaskDetail } from '../api/tasks';
import { formatDateTime } from '../utils/dateFormat';
import TaskCommentsPanel from '../components/TaskCommentsPanel';

export default function TaskEditorPage() {
    const { id } = useParams<{ id: string }>();
    const navigate = useNavigate();

    const [task, setTask] = useState<ScheduledTaskDetail | null>(null);
    const [description, setDescription] = useState('');
    const [cron, setCron] = useState('');
    const [agent, setAgent] = useState('');
    const [command, setCommand] = useState('');
    const [enabled, setEnabled] = useState(true);
    const [isOneOff, setIsOneOff] = useState(false);
    const [prompt, setPrompt] = useState('');
    const [channelType, setChannelType] = useState<'web' | 'telegram'>('web');
    const [channelKey, setChannelKey] = useState('');
    const [error, setError] = useState<string | null>(null);
    const [success, setSuccess] = useState<string | null>(null);
    const [deleteOpen, setDeleteOpen] = useState(false);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        if (!id) return;
        getTask(id)
            .then((t) => {
                setTask(t);
                setDescription(t.description);
                setCron(t.cron);
                setAgent(t.agent);
                setCommand(t.command ?? '');
                setEnabled(t.enabled);
                setIsOneOff(t.isOneOff);
                setPrompt(t.prompt);
                setChannelType(t.channelType?.toLowerCase() === 'telegram' ? 'telegram' : 'web');
                setChannelKey(t.channelKey ?? '');
                setLoading(false);
            })
            .catch(() => {
                setError('Task not found');
                setLoading(false);
            });
    }, [id]);

    const handleSave = async () => {
        try {
            setError(null);
            setSuccess(null);
            if (channelType === 'telegram') {
                if (!channelKey.trim()) {
                    setError('Telegram chat ID is required.');
                    return;
                }
                if (!/^-?\d+$/.test(channelKey.trim())) {
                    setError('Telegram chat ID must be a numeric value.');
                    return;
                }
            }
            await updateTask(id!, {
                description,
                cron,
                prompt,
                enabled,
                isOneOff,
                agent,
                command: task?.taskType === 'command' ? command : undefined,
                channelType,
                channelKey: channelType === 'telegram'
                    ? channelKey.trim()
                    : `web:${agent || 'system'}`,
            });
            setSuccess('Task saved successfully.');
        } catch (e: unknown) {
            setError(e instanceof Error ? e.message : 'Save failed');
        }
    };

    const handleDelete = async () => {
        if (!id) return;
        await deleteTask(id);
        navigate('/tasks');
    };

    if (loading) {
        return (
            <Box>
                <Typography color="text.secondary">Loading…</Typography>
            </Box>
        );
    }

    if (!task) {
        return (
            <Box>
                <Alert severity="error">Task not found.</Alert>
                <Button sx={{ mt: 2 }} onClick={() => navigate('/tasks')}>Back to Tasks</Button>
            </Box>
        );
    }

    const isCommand = task.taskType === 'command';

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
                <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
                    <Button startIcon={<ArrowBackIcon />} onClick={() => navigate('/tasks')} size="small">
                        Tasks
                    </Button>
                    <Typography variant="h5" sx={{ fontFamily: 'monospace' }}>{id}</Typography>
                    <Chip
                        label={enabled ? 'Active' : 'Disabled'}
                        color={enabled ? 'success' : 'default'}
                        size="small"
                    />
                    <Chip
                        label={isCommand ? 'Command' : 'Agent'}
                        variant="outlined"
                        size="small"
                    />
                </Stack>
                <Stack direction="row" spacing={1}>
                    <Button variant="contained" startIcon={<SaveIcon />} onClick={handleSave}>
                        Save
                    </Button>
                    <Button variant="outlined" color="error" startIcon={<DeleteIcon />} onClick={() => setDeleteOpen(true)}>
                        Delete
                    </Button>
                </Stack>
            </Box>

            {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
            {success && <Alert severity="success" sx={{ mb: 2 }}>{success}</Alert>}

            <Paper variant="outlined" sx={{ p: 3, mb: 2 }}>
                <Stack spacing={2.5}>
                    <TextField
                        label="Description"
                        value={description}
                        onChange={(e) => setDescription(e.target.value)}
                        fullWidth
                        size="small"
                    />
                    <Stack direction="row" spacing={2}>
                        <TextField
                            label="Cron Expression"
                            value={cron}
                            onChange={(e) => setCron(e.target.value)}
                            size="small"
                            sx={{ width: 220 }}
                            helperText="5-field standard cron (min hour dom mon dow)"
                        />
                        {!isCommand && (
                            <TextField
                                label="Agent"
                                value={agent}
                                onChange={(e) => setAgent(e.target.value)}
                                size="small"
                                sx={{ width: 200 }}
                            />
                        )}
                    </Stack>
                    {isCommand && (
                        <TextField
                            label="Command"
                            value={command}
                            onChange={(e) => setCommand(e.target.value)}
                            fullWidth
                            size="small"
                            helperText="Shell command to execute (runs via /bin/bash, 5-min timeout)"
                        />
                    )}
                    <Stack direction="row" spacing={3} sx={{ alignItems: 'center' }}>
                        <FormControlLabel
                            control={<Switch checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />}
                            label="Enabled"
                        />
                        <FormControlLabel
                            control={<Switch checked={isOneOff} onChange={(e) => setIsOneOff(e.target.checked)} />}
                            label="One-off (delete after run)"
                        />
                    </Stack>

                    <Box>
                        <Typography variant="subtitle2" sx={{ mb: 1 }}>Deliver Result To</Typography>
                        <Stack direction="row" spacing={2} sx={{ alignItems: 'flex-start' }}>
                            <ToggleButtonGroup
                                value={channelType}
                                exclusive
                                onChange={(_, v) => { if (v) setChannelType(v); }}
                                size="small"
                            >
                                <ToggleButton value="web">Web chat</ToggleButton>
                                <ToggleButton value="telegram">Telegram</ToggleButton>
                            </ToggleButtonGroup>
                            {channelType === 'telegram' && (
                                <TextField
                                    label="Chat ID"
                                    value={channelKey}
                                    onChange={(e) => setChannelKey(e.target.value)}
                                    size="small"
                                    sx={{ width: 200 }}
                                    helperText="Must be in Telegram:AllowedChatIds"
                                    required
                                />
                            )}
                        </Stack>
                    </Box>

                    <Stack direction="row" spacing={3}>
                        <Typography variant="caption" color="text.secondary">
                            Created: {formatDateTime(task.created)}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                            Next run: {formatDateTime(task.nextRun)}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                            Last run: {formatDateTime(task.lastRun)}
                        </Typography>
                    </Stack>
                </Stack>
            </Paper>

            <Typography variant="subtitle2" sx={{ mb: 1 }}>
                {isCommand ? 'Prompt (optional)' : 'Prompt'}
            </Typography>
            <Paper variant="outlined" sx={{ height: 'calc(100vh - 480px)', minHeight: 200, overflow: 'hidden' }}>
                <Editor
                    height="100%"
                    defaultLanguage="markdown"
                    value={prompt}
                    onChange={(v) => setPrompt(v ?? '')}
                    theme="vs-dark"
                    options={{ minimap: { enabled: false }, wordWrap: 'on', fontSize: 14 }}
                />
            </Paper>

            <Dialog open={deleteOpen} onClose={() => setDeleteOpen(false)}>
                <DialogTitle>Delete task "{description || id}"?</DialogTitle>
                <DialogActions>
                    <Button onClick={() => setDeleteOpen(false)}>Cancel</Button>
                    <Button color="error" onClick={handleDelete}>Delete</Button>
                </DialogActions>
            </Dialog>

            {id && <TaskCommentsPanel taskId={id} />}
        </Box>
    );
}
