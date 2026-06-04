import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
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
import Alert from '@mui/material/Alert';
import SaveIcon from '@mui/icons-material/Save';
import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import Editor from '@monaco-editor/react';
import { createTask } from '../api/tasks';

export default function TaskCreatePage() {
    const navigate = useNavigate();

    const [taskType, setTaskType] = useState<'agent' | 'command'>('agent');
    const [description, setDescription] = useState('');
    const [cron, setCron] = useState('');
    const [agent, setAgent] = useState('');
    const [command, setCommand] = useState('');
    const [enabled, setEnabled] = useState(true);
    const [isOneOff, setIsOneOff] = useState(false);
    const [prompt, setPrompt] = useState('');
    const [error, setError] = useState<string | null>(null);
    const [saving, setSaving] = useState(false);

    const handleCreate = async () => {
        setError(null);

        if (!cron.trim()) {
            setError('Cron expression is required.');
            return;
        }

        if (taskType === 'agent' && !agent.trim()) {
            setError('Agent name is required for agent tasks.');
            return;
        }

        if (taskType === 'command' && !command.trim()) {
            setError('Command is required for command tasks.');
            return;
        }

        try {
            setSaving(true);
            const result = await createTask({
                cron: cron.trim(),
                taskType,
                agent: taskType === 'agent' ? agent.trim() : undefined,
                command: taskType === 'command' ? command.trim() : undefined,
                prompt: prompt.trim() || undefined,
                description: description.trim() || undefined,
                isOneOff,
                enabled,
            });
            navigate(`/tasks/${result.id}`);
        } catch (e: unknown) {
            setError(e instanceof Error ? e.message : 'Failed to create task.');
        } finally {
            setSaving(false);
        }
    };

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
                <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
                    <Button startIcon={<ArrowBackIcon />} onClick={() => navigate('/tasks')} size="small">
                        Tasks
                    </Button>
                    <Typography variant="h5">New Task</Typography>
                </Stack>
                <Button
                    variant="contained"
                    startIcon={<SaveIcon />}
                    onClick={handleCreate}
                    disabled={saving}
                >
                    Create
                </Button>
            </Box>

            {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}

            <Paper variant="outlined" sx={{ p: 3, mb: 2 }}>
                <Stack spacing={2.5}>
                    <Box>
                        <Typography variant="subtitle2" sx={{ mb: 1 }}>Task Type</Typography>
                        <ToggleButtonGroup
                            value={taskType}
                            exclusive
                            onChange={(_, v) => { if (v) setTaskType(v); }}
                            size="small"
                        >
                            <ToggleButton value="agent">Agent</ToggleButton>
                            <ToggleButton value="command">Command</ToggleButton>
                        </ToggleButtonGroup>
                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                            {taskType === 'agent'
                                ? 'Runs an agent with the prompt below.'
                                : 'Runs a shell command directly (no agent needed).'}
                        </Typography>
                    </Box>

                    <TextField
                        label="Description"
                        value={description}
                        onChange={(e) => setDescription(e.target.value)}
                        fullWidth
                        size="small"
                        placeholder="Brief description of what this task does"
                    />

                    <Stack direction="row" spacing={2}>
                        <TextField
                            label="Cron Expression"
                            value={cron}
                            onChange={(e) => setCron(e.target.value)}
                            size="small"
                            sx={{ width: 220 }}
                            helperText="5-field standard cron (min hour dom mon dow)"
                            required
                        />
                        {taskType === 'agent' && (
                            <TextField
                                label="Agent"
                                value={agent}
                                onChange={(e) => setAgent(e.target.value)}
                                size="small"
                                sx={{ width: 200 }}
                                required
                            />
                        )}
                    </Stack>

                    {taskType === 'command' && (
                        <TextField
                            label="Command"
                            value={command}
                            onChange={(e) => setCommand(e.target.value)}
                            fullWidth
                            size="small"
                            placeholder="e.g. curl -s https://api.example.com/data >> /tmp/output.csv"
                            helperText="Shell command to execute (runs via /bin/bash, 5-min timeout)"
                            required
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
                </Stack>
            </Paper>

            <Typography variant="subtitle2" sx={{ mb: 1 }}>
                {taskType === 'agent' ? 'Prompt' : 'Prompt (optional — used as task description in logs)'}
            </Typography>
            <Paper variant="outlined" sx={{ height: 'calc(100vh - 520px)', minHeight: 200, overflow: 'hidden' }}>
                <Editor
                    height="100%"
                    defaultLanguage="markdown"
                    value={prompt}
                    onChange={(v) => setPrompt(v ?? '')}
                    theme="vs-dark"
                    options={{ minimap: { enabled: false }, wordWrap: 'on', fontSize: 14 }}
                />
            </Paper>
        </Box>
    );
}
