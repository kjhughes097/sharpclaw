import { useEffect, useState } from 'react';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import Chip from '@mui/material/Chip';
import Tooltip from '@mui/material/Tooltip';
import Dialog from '@mui/material/Dialog';
import DialogTitle from '@mui/material/DialogTitle';
import DialogActions from '@mui/material/DialogActions';
import Alert from '@mui/material/Alert';
import EditIcon from '@mui/icons-material/Edit';
import DeleteIcon from '@mui/icons-material/Delete';
import SaveIcon from '@mui/icons-material/Save';
import CloseIcon from '@mui/icons-material/Close';
import SendIcon from '@mui/icons-material/Send';
import {
    getTicketComments,
    createTicketComment,
    updateTicketComment,
    deleteTicketComment,
    type TicketComment,
} from '../api/projects';
import { formatDateTime } from '../utils/dateFormat';

const AUTHOR_KEY = 'sharpclaw.commentAuthor';

function loadAuthor(): string {
    try {
        return localStorage.getItem(AUTHOR_KEY) || 'user';
    } catch {
        return 'user';
    }
}

function saveAuthor(value: string) {
    try {
        localStorage.setItem(AUTHOR_KEY, value);
    } catch { /* ignore */ }
}

interface Props {
    projectId: string;
    ticketId: string;
}

export default function TicketCommentsPanel({ projectId, ticketId }: Props) {
    const [comments, setComments] = useState<TicketComment[]>([]);
    const [author, setAuthor] = useState(loadAuthor());
    const [content, setContent] = useState('');
    const [editingId, setEditingId] = useState<string | null>(null);
    const [editingContent, setEditingContent] = useState('');
    const [deleteTarget, setDeleteTarget] = useState<TicketComment | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [loading, setLoading] = useState(true);

    const reload = async () => {
        try {
            const data = await getTicketComments(projectId, ticketId);
            setComments(data);
            setError(null);
        } catch (e) {
            setError(e instanceof Error ? e.message : 'Failed to load comments');
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        setLoading(true);
        reload();
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [projectId, ticketId]);

    const handleAuthorChange = (value: string) => {
        setAuthor(value);
        saveAuthor(value);
    };

    const handleAdd = async () => {
        if (!content.trim()) return;
        try {
            await createTicketComment(projectId, ticketId, content.trim(), author.trim() || 'user');
            setContent('');
            await reload();
        } catch (e) {
            setError(e instanceof Error ? e.message : 'Failed to add comment');
        }
    };

    const startEdit = (c: TicketComment) => {
        setEditingId(c.id);
        setEditingContent(c.content);
    };

    const cancelEdit = () => {
        setEditingId(null);
        setEditingContent('');
    };

    const saveEdit = async (c: TicketComment) => {
        if (!editingContent.trim()) return;
        try {
            await updateTicketComment(projectId, ticketId, c.id, editingContent.trim(), c.author);
            cancelEdit();
            await reload();
        } catch (e) {
            setError(e instanceof Error ? e.message : 'Failed to update comment');
        }
    };

    const confirmDelete = async () => {
        if (!deleteTarget) return;
        try {
            await deleteTicketComment(projectId, ticketId, deleteTarget.id, deleteTarget.author);
            setDeleteTarget(null);
            await reload();
        } catch (e) {
            setError(e instanceof Error ? e.message : 'Failed to delete comment');
        }
    };

    const isAgent = (a: string) => a.toLowerCase() !== 'user' && a.toLowerCase() !== 'me' && a.toLowerCase() !== 'ken';

    return (
        <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography variant="subtitle1" sx={{ mb: 1.5, fontWeight: 600 }}>
                Comments ({comments.length})
            </Typography>

            {error && <Alert severity="error" sx={{ mb: 2 }} onClose={() => setError(null)}>{error}</Alert>}

            {loading ? (
                <Typography variant="body2" color="text.secondary">Loading…</Typography>
            ) : comments.length === 0 ? (
                <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                    No comments yet.
                </Typography>
            ) : (
                <Stack spacing={1.5} sx={{ mb: 2 }}>
                    {comments.map((c) => (
                        <Box
                            key={c.id}
                            sx={{
                                p: 1.5,
                                border: 1,
                                borderColor: 'divider',
                                borderRadius: 1,
                                bgcolor: isAgent(c.author) ? 'action.hover' : 'background.default',
                            }}
                        >
                            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
                                <Chip
                                    label={c.author}
                                    size="small"
                                    color={isAgent(c.author) ? 'primary' : 'default'}
                                    variant={isAgent(c.author) ? 'filled' : 'outlined'}
                                />
                                <Typography variant="caption" color="text.secondary">
                                    {formatDateTime(c.created)}
                                </Typography>
                                {c.updated && (
                                    <Tooltip title={`Edited ${formatDateTime(c.updated)}`}>
                                        <Typography variant="caption" color="text.secondary" sx={{ fontStyle: 'italic' }}>
                                            (edited)
                                        </Typography>
                                    </Tooltip>
                                )}
                                <Box sx={{ flex: 1 }} />
                                {editingId === c.id ? (
                                    <>
                                        <IconButton size="small" onClick={() => saveEdit(c)} aria-label="Save edit">
                                            <SaveIcon fontSize="small" />
                                        </IconButton>
                                        <IconButton size="small" onClick={cancelEdit} aria-label="Cancel edit">
                                            <CloseIcon fontSize="small" />
                                        </IconButton>
                                    </>
                                ) : (
                                    <>
                                        <IconButton
                                            size="small"
                                            onClick={() => startEdit(c)}
                                            aria-label="Edit comment"
                                            disabled={c.author !== author.trim()}
                                            title={c.author !== author.trim() ? 'Only the author can edit' : 'Edit'}
                                        >
                                            <EditIcon fontSize="small" />
                                        </IconButton>
                                        <IconButton
                                            size="small"
                                            onClick={() => setDeleteTarget(c)}
                                            aria-label="Delete comment"
                                            disabled={c.author !== author.trim()}
                                            title={c.author !== author.trim() ? 'Only the author can delete' : 'Delete'}
                                        >
                                            <DeleteIcon fontSize="small" />
                                        </IconButton>
                                    </>
                                )}
                            </Stack>
                            {editingId === c.id ? (
                                <TextField
                                    fullWidth
                                    multiline
                                    minRows={2}
                                    value={editingContent}
                                    onChange={(e) => setEditingContent(e.target.value)}
                                    size="small"
                                />
                            ) : (
                                <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>
                                    {c.content}
                                </Typography>
                            )}
                        </Box>
                    ))}
                </Stack>
            )}

            <Stack spacing={1}>
                <Stack direction="row" spacing={1}>
                    <TextField
                        label="Author"
                        value={author}
                        onChange={(e) => handleAuthorChange(e.target.value)}
                        size="small"
                        sx={{ width: 160 }}
                    />
                    <TextField
                        label="Add a comment"
                        value={content}
                        onChange={(e) => setContent(e.target.value)}
                        size="small"
                        fullWidth
                        multiline
                        minRows={1}
                        maxRows={6}
                        onKeyDown={(e) => {
                            if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                                e.preventDefault();
                                handleAdd();
                            }
                        }}
                    />
                    <Button
                        variant="contained"
                        startIcon={<SendIcon />}
                        onClick={handleAdd}
                        disabled={!content.trim()}
                    >
                        Post
                    </Button>
                </Stack>
                <Typography variant="caption" color="text.secondary">
                    Ctrl+Enter to post. Comments are sorted oldest first.
                </Typography>
            </Stack>

            <Dialog open={deleteTarget !== null} onClose={() => setDeleteTarget(null)}>
                <DialogTitle>Delete this comment?</DialogTitle>
                <DialogActions>
                    <Button onClick={() => setDeleteTarget(null)}>Cancel</Button>
                    <Button color="error" onClick={confirmDelete}>Delete</Button>
                </DialogActions>
            </Dialog>
        </Paper>
    );
}
