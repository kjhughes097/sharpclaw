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
import { getMcpRaw, updateMcp, deleteMcp } from '../api/mcps';

const defaultMcp = JSON.stringify({
    transport: 'stdio',
    command: 'npx',
    args: ['-y', '@example/mcp-server'],
}, null, 2);

export default function McpEditorPage() {
    const { name } = useParams<{ name: string }>();
    const navigate = useNavigate();
    const isNew = !name;

    const [mcpName, setMcpName] = useState('');
    const [content, setContent] = useState(defaultMcp);
    const [error, setError] = useState<string | null>(null);
    const [deleteOpen, setDeleteOpen] = useState(false);

    useEffect(() => {
        if (name) {
            getMcpRaw(name).then((raw) => {
                setMcpName(name);
                try { setContent(JSON.stringify(JSON.parse(raw), null, 2)); }
                catch { setContent(raw); }
            }).catch(() => setError('MCP not found'));
        }
    }, [name]);

    const handleSave = async () => {
        try {
            setError(null);
            JSON.parse(content); // validate JSON
            if (isNew) {
                if (!mcpName.trim()) { setError('Name is required'); return; }
                await fetch('/api/mcps', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ name: mcpName.trim(), config: JSON.parse(content) }),
                }).then(r => { if (!r.ok) throw new Error(`${r.status}`); });
                navigate(`/mcps/${mcpName.trim()}`);
            } else {
                await updateMcp(name!, content);
            }
        } catch (e: unknown) {
            setError(e instanceof Error ? e.message : 'Save failed');
        }
    };

    const handleDelete = async () => {
        if (!name) return;
        await deleteMcp(name);
        navigate('/mcps');
    };

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
                <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
                    {isNew ? (
                        <TextField
                            label="MCP Name"
                            size="small"
                            value={mcpName}
                            onChange={(e) => setMcpName(e.target.value)}
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
                    defaultLanguage="json"
                    value={content}
                    onChange={(v) => setContent(v ?? '')}
                    theme="vs-dark"
                    options={{ minimap: { enabled: false }, fontSize: 14 }}
                />
            </Paper>
            <Dialog open={deleteOpen} onClose={() => setDeleteOpen(false)}>
                <DialogTitle>Delete MCP "{name}"?</DialogTitle>
                <DialogActions>
                    <Button onClick={() => setDeleteOpen(false)}>Cancel</Button>
                    <Button color="error" onClick={handleDelete}>Delete</Button>
                </DialogActions>
            </Dialog>
        </Box>
    );
}
