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
import { getSkill, updateSkill, createSkill, deleteSkill } from '../api/skills';

const defaultSkill = '---\ndescription: A new skill\n---\n\nSkill prompt content goes here.\n';

export default function SkillEditorPage() {
    const { name } = useParams<{ name: string }>();
    const navigate = useNavigate();
    const isNew = !name;

    const [skillName, setSkillName] = useState('');
    const [content, setContent] = useState(defaultSkill);
    const [error, setError] = useState<string | null>(null);
    const [deleteOpen, setDeleteOpen] = useState(false);

    useEffect(() => {
        if (name) {
            getSkill(name).then((s) => {
                setSkillName(s.name);
                setContent(s.rawContent ?? '');
            }).catch(() => setError('Skill not found'));
        }
    }, [name]);

    const handleSave = async () => {
        try {
            setError(null);
            if (isNew) {
                if (!skillName.trim()) { setError('Name is required'); return; }
                await createSkill(skillName.trim(), content);
                navigate(`/skills/${skillName.trim()}`);
            } else {
                await updateSkill(name!, content);
            }
        } catch (e: unknown) {
            setError(e instanceof Error ? e.message : 'Save failed');
        }
    };

    const handleDelete = async () => {
        if (!name) return;
        await deleteSkill(name);
        navigate('/skills');
    };

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
                <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
                    {isNew ? (
                        <TextField
                            label="Skill Name"
                            size="small"
                            value={skillName}
                            onChange={(e) => setSkillName(e.target.value)}
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
                <DialogTitle>Delete skill "{name}"?</DialogTitle>
                <DialogActions>
                    <Button onClick={() => setDeleteOpen(false)}>Cancel</Button>
                    <Button color="error" onClick={handleDelete}>Delete</Button>
                </DialogActions>
            </Dialog>
        </Box>
    );
}
