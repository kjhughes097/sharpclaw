import { useEffect, useState } from 'react';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import Paper from '@mui/material/Paper';
import TextField from '@mui/material/TextField';
import Grid from '@mui/material/Grid';
import Alert from '@mui/material/Alert';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import CheckCircleIcon from '@mui/icons-material/CheckCircle';
import CancelIcon from '@mui/icons-material/Cancel';
import SaveIcon from '@mui/icons-material/Save';
import { getConfig, updateConfig } from '../api/config';
import type { ConfigData } from '../api/config';

function StatusChip({ configured }: { configured: boolean }) {
    return configured
        ? <Chip icon={<CheckCircleIcon />} label="Configured" color="success" size="small" />
        : <Chip icon={<CancelIcon />} label="Not configured" color="error" size="small" variant="outlined" />;
}

export default function ConfigPage() {
    const [config, setConfig] = useState<ConfigData | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [success, setSuccess] = useState(false);

    // Editable values
    const [anthropicKey, setAnthropicKey] = useState('');
    const [anthropicModel, setAnthropicModel] = useState('');
    const [anthropicMaxTokens, setAnthropicMaxTokens] = useState(8192);
    const [telegramToken, setTelegramToken] = useState('');
    const [telegramDefaultAgent, setTelegramDefaultAgent] = useState('');
    const [defaultAgent, setDefaultAgent] = useState('');
    const [otelEndpoint, setOtelEndpoint] = useState('');

    useEffect(() => {
        getConfig().then((c) => {
            setConfig(c);
            setAnthropicModel(c.anthropic.defaultModel);
            setAnthropicMaxTokens(c.anthropic.maxTokens);
            setTelegramDefaultAgent(c.telegram.defaultAgent);
            setDefaultAgent(c.sharpClaw.defaultAgent);
            setOtelEndpoint(c.openTelemetry.endpoint);
        });
    }, []);

    const handleSave = async () => {
        try {
            setError(null);
            setSuccess(false);
            const payload: Record<string, unknown> = {
                SharpClaw: { DefaultAgent: defaultAgent },
                OpenTelemetry: { Endpoint: otelEndpoint },
                Anthropic: { DefaultModel: anthropicModel, MaxTokens: anthropicMaxTokens },
                Telegram: { DefaultAgent: telegramDefaultAgent },
            };
            if (anthropicKey) payload.Anthropic = { ...payload.Anthropic as object, ApiKey: anthropicKey };
            if (telegramToken) payload.Telegram = { ...payload.Telegram as object, BotToken: telegramToken };
            await updateConfig(payload);
            setSuccess(true);
        } catch (e: unknown) {
            setError(e instanceof Error ? e.message : 'Save failed');
        }
    };

    if (!config) return <Typography>Loading...</Typography>;

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
                <Typography variant="h4">Configuration</Typography>
                <Button variant="contained" startIcon={<SaveIcon />} onClick={handleSave}>
                    Save
                </Button>
            </Box>
            {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
            {success && <Alert severity="success" sx={{ mb: 2 }}>Configuration saved. Restart the service to apply changes.</Alert>}

            <Grid container spacing={3}>
                {/* SharpClaw */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 3 }}>
                        <Typography variant="h6" gutterBottom>SharpClaw</Typography>
                        <Stack spacing={2}>
                            <TextField label="Default Agent" value={defaultAgent} onChange={e => setDefaultAgent(e.target.value)} fullWidth size="small" />
                            <TextField label="Workspace Path" value={config.sharpClaw.workspacePath} fullWidth size="small" disabled />
                            <TextField label="Chat History Limit" value={config.sharpClaw.chatHistoryLimit} fullWidth size="small" disabled />
                        </Stack>
                    </Paper>
                </Grid>

                {/* Anthropic */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 3 }}>
                        <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
                            <Typography variant="h6">Anthropic</Typography>
                            <StatusChip configured={config.anthropic.isConfigured} />
                        </Stack>
                        <Stack spacing={2}>
                            <TextField label="API Key" placeholder={config.anthropic.apiKey ?? 'Not set'} value={anthropicKey} onChange={e => setAnthropicKey(e.target.value)} fullWidth size="small" type="password" />
                            <TextField label="Default Model" value={anthropicModel} onChange={e => setAnthropicModel(e.target.value)} fullWidth size="small" />
                            <TextField label="Max Tokens" type="number" value={anthropicMaxTokens} onChange={e => setAnthropicMaxTokens(Number(e.target.value))} fullWidth size="small" />
                        </Stack>
                    </Paper>
                </Grid>

                {/* Telegram */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 3 }}>
                        <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 1 }}>
                            <Typography variant="h6">Telegram</Typography>
                            <StatusChip configured={config.telegram.isConfigured} />
                        </Stack>
                        <Stack spacing={2}>
                            <TextField label="Bot Token" placeholder={config.telegram.botToken ?? 'Not set'} value={telegramToken} onChange={e => setTelegramToken(e.target.value)} fullWidth size="small" type="password" />
                            <TextField label="Default Agent" value={telegramDefaultAgent} onChange={e => setTelegramDefaultAgent(e.target.value)} fullWidth size="small" />
                            <TextField label="Allowed Users" value={config.telegram.allowedUsers.join(', ')} fullWidth size="small" disabled />
                        </Stack>
                    </Paper>
                </Grid>

                {/* OpenTelemetry */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 3 }}>
                        <Typography variant="h6" gutterBottom>OpenTelemetry</Typography>
                        <Stack spacing={2}>
                            <TextField label="Endpoint" value={otelEndpoint} onChange={e => setOtelEndpoint(e.target.value)} fullWidth size="small" />
                        </Stack>
                    </Paper>
                </Grid>
            </Grid>
        </Box>
    );
}
