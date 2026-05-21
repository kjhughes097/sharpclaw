import { useEffect, useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Dialog from '@mui/material/Dialog';
import DialogTitle from '@mui/material/DialogTitle';
import DialogActions from '@mui/material/DialogActions';
import Alert from '@mui/material/Alert';
import SaveIcon from '@mui/icons-material/Save';
import DeleteIcon from '@mui/icons-material/Delete';
import Editor from '@monaco-editor/react';
import { getAgent, updateAgent, createAgent, deleteAgent } from '../api/agents';

export default function AgentEditorPage() {
    const { name } = useParams<{ name: string }>();
    const navigate = useNavigate();
    const isNew = !name;

    const [agentName, setAgentName] = useState('');
    const [content, setContent] = useState('---\nllm: copilot\nmodel: claude-sonnet-4.5\ndescription: \ntools: []\nmcp_servers: []\nskills: []\nsub_agents: []\n---\n\nYou are a helpful assistant.\n');
    const [error, setError] = useState<string | null>(null);
    const [deleteOpen, setDeleteOpen] = useState(false);

    useEffect(() => {
        if (name) {
            getAgent(name).then((a) => {
                setAgentName(a.name);
                setContent(a.rawContent ?? '');
            }).catch(() => setError('Agent not found'));
        }
    }, [name]);

    const handleSave = async () => {
        try {
            setError(null);
            if (isNew) {
                if (!agentName.trim()) { setError('Name is required'); return; }
                await createAgent(agentName.trim(), content);
                navigate(`/agents/${agentName.trim()}`);
            } else {
                await updateAgent(name!, content);
            }
        } catch (e: unknown) {
            setError(e instanceof Error ? e.message : 'Save failed');
        }
    };

    const handleDelete = async () => {
        if (!name) return;
        await deleteAgent(name);
        navigate('/agents');
    };

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
                <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
                    {isNew ? (
                        <TextField
                            label="Agent Name"
                            size="small"
                            value={agentName}
                            onChange={(e) => setAgentName(e.target.value)}
                            sx={{ width: 200 }}
                        />
                    ) : (
                        <Typography variant="h4">{name}</Typography>
                    )}
                </Stack>
                <Stack direction="row" spacing={1}>
                    <Button variant="contained" startIcon={<SaveIcon />} onClick={handleSave}>
                        Save
                    </Button>
                    {!isNew && (
                        <Button variant="outlined" color="error" startIcon={<DeleteIcon />} onClick={() => setDeleteOpen(true)}>
                            Delete
                        </Button>
                    )}
                </Stack>
            </Box>
            {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
            <Paper variant="outlined" sx={{ height: 'calc(100vh - 200px)', overflow: 'hidden' }}>
                <Editor
                    height="100%"
                    defaultLanguage="markdown"
                    value={content}
                    onChange={(v) => setContent(v ?? '')}
                    theme="vs-dark"
                    options={{ minimap: { enabled: false }, wordWrap: 'on', fontSize: 14 }}
                />
            </Paper>
            <Dialog open={deleteOpen} onClose={() => setDeleteOpen(false)}>
                <DialogTitle>Delete agent "{name}"?</DialogTitle>
                <DialogActions>
                    <Button onClick={() => setDeleteOpen(false)}>Cancel</Button>
                    <Button color="error" onClick={handleDelete}>Delete</Button>
                </DialogActions>
            </Dialog>
        </Box>
    );
}
