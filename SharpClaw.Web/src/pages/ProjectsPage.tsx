import { useEffect, useState, useCallback, type DragEvent } from 'react';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import Paper from '@mui/material/Paper';
import Dialog from '@mui/material/Dialog';
import DialogTitle from '@mui/material/DialogTitle';
import DialogContent from '@mui/material/DialogContent';
import DialogActions from '@mui/material/DialogActions';
import Button from '@mui/material/Button';
import TextField from '@mui/material/TextField';
import Editor from '@monaco-editor/react';
import { getProjects, getProjectTickets, updateTicketStatus, updateTicket, improveTicket } from '../api/projects';
import type { ProjectSummary, TicketSummary } from '../api/projects';

const STATUSES = [
    { key: 'idea', label: 'Idea' },
    { key: 'planning', label: 'Planning' },
    { key: 'in_progress', label: 'In Progress' },
    { key: 'for_review', label: 'For Review' },
    { key: 'done', label: 'Done' },
];

const PROJECT_COLOURS: string[] = [
    '#1976d2', '#7b1fa2', '#388e3c', '#f57c00', '#c62828',
    '#00838f', '#4527a0', '#558b2f', '#d84315', '#283593',
];

function getProjectColour(projectId: string, projects: ProjectSummary[]): string {
    const idx = projects.findIndex(p => p.id === projectId);
    return PROJECT_COLOURS[idx % PROJECT_COLOURS.length];
}

export default function ProjectsPage() {
    const [projects, setProjects] = useState<ProjectSummary[]>([]);
    const [tickets, setTickets] = useState<TicketSummary[]>([]);
    const [draggedTicketId, setDraggedTicketId] = useState<string | null>(null);
    const [dragOverStatus, setDragOverStatus] = useState<string | null>(null);
    const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
    const [editingTicket, setEditingTicket] = useState<TicketSummary | null>(null);
    const [editTitle, setEditTitle] = useState('');
    const [editDescription, setEditDescription] = useState('');
    const [saving, setSaving] = useState(false);
    const [improving, setImproving] = useState(false);

    useEffect(() => {
        getProjects().then(async (projs) => {
            setProjects(projs);
            const allTickets = await Promise.all(
                projs.map(p => getProjectTickets(p.id))
            );
            setTickets(allTickets.flat());
        });
    }, []);

    const handleDragStart = useCallback((e: DragEvent, ticketId: string) => {
        e.dataTransfer.setData('text/plain', ticketId);
        e.dataTransfer.effectAllowed = 'move';
        setDraggedTicketId(ticketId);
    }, []);

    const handleDragOver = useCallback((e: DragEvent, status: string) => {
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        setDragOverStatus(status);
    }, []);

    const handleDragLeave = useCallback(() => {
        setDragOverStatus(null);
    }, []);

    const handleDrop = useCallback((e: DragEvent, newStatus: string) => {
        e.preventDefault();
        setDragOverStatus(null);
        setDraggedTicketId(null);

        const ticketId = e.dataTransfer.getData('text/plain');
        const ticket = tickets.find(t => t.id === ticketId);
        if (!ticket || ticket.status === newStatus) return;

        // Optimistic update
        setTickets(prev =>
            prev.map(t => t.id === ticketId ? { ...t, status: newStatus } : t)
        );

        updateTicketStatus(ticket.projectId, ticketId, newStatus).catch(() => {
            // Revert on failure
            setTickets(prev =>
                prev.map(t => t.id === ticketId ? { ...t, status: ticket.status } : t)
            );
        });
    }, [tickets]);

    const handleDragEnd = useCallback(() => {
        setDraggedTicketId(null);
        setDragOverStatus(null);
    }, []);

    const handleTicketClick = useCallback((ticket: TicketSummary) => {
        setEditingTicket(ticket);
        setEditTitle(ticket.title);
        setEditDescription(ticket.description ?? '');
    }, []);

    const handleEditorClose = useCallback(() => {
        setEditingTicket(null);
    }, []);

    const handleEditorSave = useCallback(async () => {
        if (!editingTicket) return;
        setSaving(true);
        try {
            const data: { title?: string; description?: string } = {};
            if (editTitle !== editingTicket.title) data.title = editTitle;
            if (editDescription !== (editingTicket.description ?? '')) data.description = editDescription;

            if (Object.keys(data).length > 0) {
                const updated = await updateTicket(editingTicket.projectId, editingTicket.id, data);
                setTickets(prev => prev.map(t => t.id === updated.id ? updated : t));
            }
            setEditingTicket(null);
        } finally {
            setSaving(false);
        }
    }, [editingTicket, editTitle, editDescription]);

    const handleImprove = useCallback(async () => {
        if (!editingTicket) return;
        setImproving(true);
        try {
            const result = await improveTicket(editingTicket.projectId, editingTicket.id);
            setEditDescription(result.description);
        } finally {
            setImproving(false);
        }
    }, [editingTicket]);

    const filteredTickets = selectedProjectId
        ? tickets.filter(t => t.projectId === selectedProjectId)
        : tickets;

    const ticketsByStatus = (status: string) =>
        filteredTickets.filter(t => t.status === status);

    return (
        <Box>
            <Typography variant="h4" gutterBottom>Projects</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                Kanban board showing tickets across all projects.
            </Typography>

            {projects.length > 0 && (
                <Stack direction="row" spacing={1} sx={{ mb: 2, flexWrap: 'wrap', gap: 1 }}>
                    <Chip
                        label="All"
                        variant={selectedProjectId === null ? 'filled' : 'outlined'}
                        color={selectedProjectId === null ? 'primary' : 'default'}
                        onClick={() => setSelectedProjectId(null)}
                        sx={{ fontWeight: 600 }}
                    />
                    {projects.map((project, idx) => (
                        <Chip
                            key={project.id}
                            label={project.title}
                            variant={selectedProjectId === project.id ? 'filled' : 'outlined'}
                            onClick={() => setSelectedProjectId(
                                selectedProjectId === project.id ? null : project.id
                            )}
                            sx={{
                                fontWeight: 500,
                                ...(selectedProjectId === project.id && {
                                    bgcolor: PROJECT_COLOURS[idx % PROJECT_COLOURS.length],
                                    color: '#fff',
                                    '&:hover': {
                                        bgcolor: PROJECT_COLOURS[idx % PROJECT_COLOURS.length],
                                        opacity: 0.85,
                                    },
                                }),
                                ...(selectedProjectId !== project.id && {
                                    borderColor: PROJECT_COLOURS[idx % PROJECT_COLOURS.length],
                                    color: PROJECT_COLOURS[idx % PROJECT_COLOURS.length],
                                }),
                            }}
                        />
                    ))}
                </Stack>
            )}

            {projects.length === 0 && (
                <Typography color="text.secondary">No projects found.</Typography>
            )}

            <Box sx={{ display: 'flex', gap: 2, overflowX: 'auto', pb: 2 }}>
                {STATUSES.map(({ key, label }) => (
                    <Paper
                        key={key}
                        variant="outlined"
                        onDragOver={(e) => handleDragOver(e as unknown as DragEvent, key)}
                        onDragLeave={handleDragLeave}
                        onDrop={(e) => handleDrop(e as unknown as DragEvent, key)}
                        sx={{
                            flex: '1 1 0',
                            minWidth: 260,
                            p: 1.5,
                            bgcolor: dragOverStatus === key ? 'action.hover' : 'background.default',
                            transition: 'background-color 0.15s',
                        }}
                    >
                        <Stack direction="row" spacing={1} sx={{ mb: 1.5, alignItems: 'center' }}>
                            <Typography variant="subtitle2" sx={{ fontWeight: 700, textTransform: 'uppercase', letterSpacing: 0.5 }}>
                                {label}
                            </Typography>
                            <Chip label={ticketsByStatus(key).length} size="small" variant="outlined" />
                        </Stack>

                        <Stack spacing={1}>
                            {ticketsByStatus(key).map((ticket) => (
                                <Card
                                    key={ticket.id}
                                    variant="outlined"
                                    draggable
                                    onDragStart={(e) => handleDragStart(e as unknown as DragEvent, ticket.id)}
                                    onDragEnd={handleDragEnd}
                                    onClick={() => handleTicketClick(ticket)}
                                    sx={{
                                        cursor: 'grab',
                                        opacity: draggedTicketId === ticket.id ? 0.5 : 1,
                                        '&:hover': { borderColor: 'primary.main' },
                                        transition: 'opacity 0.15s, border-color 0.15s',
                                    }}
                                >
                                    <CardContent sx={{ p: 1.5, '&:last-child': { pb: 1.5 } }}>
                                        <Typography variant="caption" color="text.secondary" sx={{ fontFamily: 'monospace', fontSize: '0.7rem' }}>
                                            #{ticket.id}
                                        </Typography>
                                        <Typography variant="body2" sx={{ fontWeight: 500, mb: 0.75 }}>
                                            {ticket.title}
                                        </Typography>
                                        <Chip
                                            label={projects.find(p => p.id === ticket.projectId)?.title ?? ticket.projectId}
                                            size="small"
                                            sx={{
                                                bgcolor: getProjectColour(ticket.projectId, projects),
                                                color: '#fff',
                                                fontWeight: 500,
                                                fontSize: '0.7rem',
                                            }}
                                        />
                                    </CardContent>
                                </Card>
                            ))}
                        </Stack>
                    </Paper>
                ))}
            </Box>

            <Dialog
                open={editingTicket !== null}
                onClose={handleEditorClose}
                maxWidth="md"
                fullWidth
            >
                <DialogTitle>
                    <TextField
                        value={editTitle}
                        onChange={(e) => setEditTitle(e.target.value)}
                        variant="standard"
                        fullWidth
                        slotProps={{ input: { sx: { fontSize: '1.25rem', fontWeight: 600 } } }}
                    />
                </DialogTitle>
                <DialogContent sx={{ p: 0, height: 450 }}>
                    <Editor
                        height="100%"
                        defaultLanguage="markdown"
                        value={editDescription}
                        onChange={(v) => setEditDescription(v ?? '')}
                        theme="vs-dark"
                        options={{ minimap: { enabled: false }, wordWrap: 'on', fontSize: 14, lineNumbers: 'off' }}
                    />
                </DialogContent>
                <DialogActions>
                    <Button onClick={handleImprove} disabled={improving || saving} color="secondary">
                        {improving ? 'Improving...' : 'Improve'}
                    </Button>
                    <Box sx={{ flex: 1 }} />
                    <Button onClick={handleEditorClose}>Cancel</Button>
                    <Button variant="contained" onClick={handleEditorSave} disabled={saving || improving}>
                        {saving ? 'Saving...' : 'Save'}
                    </Button>
                </DialogActions>
            </Dialog>
        </Box>
    );
}
