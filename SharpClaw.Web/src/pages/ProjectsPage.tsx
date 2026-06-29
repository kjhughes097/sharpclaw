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
import IconButton from '@mui/material/IconButton';
import AddIcon from '@mui/icons-material/Add';
import DeleteIcon from '@mui/icons-material/Delete';
import SettingsIcon from '@mui/icons-material/Settings';
import MenuItem from '@mui/material/MenuItem';
import Autocomplete from '@mui/material/Autocomplete';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemText from '@mui/material/ListItemText';
import ListItemSecondaryAction from '@mui/material/ListItemSecondaryAction';
import useMediaQuery from '@mui/material/useMediaQuery';
import { useTheme } from '@mui/material/styles';
import Editor from '@monaco-editor/react';
import { getProjects, getProjectTickets, getUsers, getLabels, addLabel, removeLabel, createProject, deleteProject, updateTicketStatus, updateTicket, createTicket, improveTicket, moveTicket, deleteTicket } from '../api/projects';
import type { ProjectSummary, TicketSummary, User } from '../api/projects';

const STATUSES: { key: string; label: string; accent?: string }[] = [
    { key: 'idea', label: 'Idea' },
    { key: 'planning', label: 'Planning' },
    { key: 'todo', label: 'Todo' },
    { key: 'in_progress', label: 'In Progress' },
    { key: 'blocked', label: 'Blocked', accent: '#d32f2f' },
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
    const theme = useTheme();
    const isMobile = useMediaQuery(theme.breakpoints.down('md'));
    const [projects, setProjects] = useState<ProjectSummary[]>([]);
    const [tickets, setTickets] = useState<TicketSummary[]>([]);
    const [users, setUsers] = useState<User[]>([]);
    const [draggedTicketId, setDraggedTicketId] = useState<string | null>(null);
    const [dragOverStatus, setDragOverStatus] = useState<string | null>(null);
    const [selectedProjectId, setSelectedProjectId] = useState<string | null>(null);
    const [selectedAssignee, setSelectedAssignee] = useState<string | null>(null);
    const [editingTicket, setEditingTicket] = useState<TicketSummary | null>(null);
    const [editTitle, setEditTitle] = useState('');
    const [editDescription, setEditDescription] = useState('');
    const [editAssignee, setEditAssignee] = useState('');
    const [editLabels, setEditLabels] = useState<string[]>([]);
    const [editProjectId, setEditProjectId] = useState('');
    const [editStatus, setEditStatus] = useState('');
    const [saving, setSaving] = useState(false);
    const [improving, setImproving] = useState(false);
    const [confirmingDelete, setConfirmingDelete] = useState(false);
    const [creatingTicket, setCreatingTicket] = useState(false);
    const [newTicketTitle, setNewTicketTitle] = useState('');
    const [newTicketDescription, setNewTicketDescription] = useState('');
    const [newTicketProjectId, setNewTicketProjectId] = useState('');
    const [newTicketAssignee, setNewTicketAssignee] = useState('');
    const [newTicketLabels, setNewTicketLabels] = useState<string[]>([]);
    const [savingNew, setSavingNew] = useState(false);
    const [labels, setLabels] = useState<string[]>([]);
    const [managingProjects, setManagingProjects] = useState(false);
    const [newProjectTitle, setNewProjectTitle] = useState('');
    const [newProjectDescription, setNewProjectDescription] = useState('');
    const [managingLabels, setManagingLabels] = useState(false);
    const [newLabelName, setNewLabelName] = useState('');

    useEffect(() => {
        getProjects().then(async (projs) => {
            setProjects(projs);
            const allTickets = await Promise.all(
                projs.map(p => getProjectTickets(p.id))
            );
            setTickets(allTickets.flat());
        });
        getUsers().then(setUsers).catch(() => setUsers([]));
        getLabels().then(setLabels).catch(() => setLabels([]));
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
        setEditAssignee(ticket.assignee ?? '');
        setEditLabels(ticket.labels ?? []);
        setEditProjectId(ticket.projectId);
        setEditStatus(ticket.status);
    }, []);

    const handleEditorClose = useCallback(() => {
        setEditingTicket(null);
    }, []);

    const handleEditorSave = useCallback(async () => {
        if (!editingTicket) return;
        setSaving(true);
        try {
            const targetProjectId = editProjectId;
            // Handle project move first
            if (editProjectId !== editingTicket.projectId) {
                const moved = await moveTicket(editingTicket.projectId, editingTicket.id, editProjectId);
                setTickets(prev => prev.map(t => t.id === editingTicket.id ? moved : t));
            }
            const data: { title?: string; description?: string; assignee?: string; labels?: string[]; status?: string } = {};
            if (editTitle !== editingTicket.title) data.title = editTitle;
            if (editDescription !== (editingTicket.description ?? '')) data.description = editDescription;
            if (editAssignee !== (editingTicket.assignee ?? '')) data.assignee = editAssignee || undefined;
            const existingLabels = editingTicket.labels ?? [];
            if (JSON.stringify(editLabels.slice().sort()) !== JSON.stringify(existingLabels.slice().sort())) data.labels = editLabels;
            if (editStatus !== editingTicket.status) data.status = editStatus;
            if (Object.keys(data).length > 0) {
                const updated = await updateTicket(targetProjectId, editingTicket.id, data);
                setTickets(prev => prev.map(t => t.id === updated.id ? updated : t));
            }
            setEditingTicket(null);
        } finally {
            setSaving(false);
        }
    }, [editingTicket, editTitle, editDescription, editAssignee, editLabels, editProjectId, editStatus]);

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

    const handleDeleteTicket = useCallback(async () => {
        if (!editingTicket) return;
        setSaving(true);
        try {
            await deleteTicket(editingTicket.projectId, editingTicket.id);
            setTickets(prev => prev.filter(t => t.id !== editingTicket.id));
            setEditingTicket(null);
            setConfirmingDelete(false);
        } finally {
            setSaving(false);
        }
    }, [editingTicket]);

    const handleOpenCreateTicket = useCallback(() => {
        setNewTicketTitle('');
        setNewTicketDescription('');
        setNewTicketProjectId(selectedProjectId ?? projects[0]?.id ?? '');
        setNewTicketAssignee('');
        setNewTicketLabels([]);
        setCreatingTicket(true);
    }, [selectedProjectId, projects]);

    const handleCreateTicket = useCallback(async () => {
        if (!newTicketProjectId || !newTicketTitle.trim()) return;
        setSavingNew(true);
        try {
            const ticket = await createTicket(newTicketProjectId, {
                title: newTicketTitle.trim(),
                description: newTicketDescription.trim() || undefined,
                assignee: newTicketAssignee || undefined,
                labels: newTicketLabels.length > 0 ? newTicketLabels : undefined,
            });
            setTickets(prev => [...prev, ticket]);
            setCreatingTicket(false);
        } finally {
            setSavingNew(false);
        }
    }, [newTicketProjectId, newTicketTitle, newTicketDescription, newTicketAssignee, newTicketLabels]);

    const filteredTickets = tickets.filter(t => {
        if (selectedProjectId && t.projectId !== selectedProjectId)
            return false;
        if (selectedAssignee && t.assignee !== selectedAssignee)
            return false;
        return true;
    });

    const ticketsByStatus = (status: string) =>
        filteredTickets.filter(t => t.status === status);

    return (
        <Box>
            <Stack direction="row" spacing={1} sx={{ mb: 1, alignItems: 'center' }}>
                <Typography variant="h4">Projects</Typography>
                <IconButton color="primary" onClick={handleOpenCreateTicket} title="New ticket" size="small">
                    <AddIcon />
                </IconButton>
                <IconButton color="default" onClick={() => setManagingProjects(true)} title="Manage projects" size="small">
                    <SettingsIcon />
                </IconButton>
                <IconButton color="default" onClick={() => setManagingLabels(true)} title="Manage labels" size="small">
                    <SettingsIcon fontSize="small" />
                </IconButton>
            </Stack>
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

            {users.length > 0 && (
                <Stack direction="row" spacing={1} sx={{ mb: 2, flexWrap: 'wrap', gap: 0.5 }}>
                    <Typography variant="caption" color="text.secondary" sx={{ alignSelf: 'center', mr: 0.5 }}>
                        Assignee:
                    </Typography>
                    <Chip
                        label="All"
                        size="small"
                        variant={selectedAssignee === null ? 'filled' : 'outlined'}
                        color={selectedAssignee === null ? 'primary' : 'default'}
                        onClick={() => setSelectedAssignee(null)}
                    />
                    {users.map((user) => (
                        <Chip
                            key={user.id}
                            label={user.name}
                            size="small"
                            variant={selectedAssignee === user.id ? 'filled' : 'outlined'}
                            color={selectedAssignee === user.id ? 'primary' : 'default'}
                            onClick={() => setSelectedAssignee(
                                selectedAssignee === user.id ? null : user.id
                            )}
                        />
                    ))}
                </Stack>
            )}

            {projects.length === 0 && (
                <Typography color="text.secondary">No projects found.</Typography>
            )}

            <Box
                sx={{
                    display: 'flex',
                    gap: 1,
                    pb: 2,
                    width: '100%',
                    overflowX: { xs: 'auto', md: 'visible' },
                    WebkitOverflowScrolling: 'touch',
                    scrollSnapType: { xs: 'x mandatory', md: 'none' },
                    mx: { xs: -1.5, sm: -2, md: 0 },
                    px: { xs: 1.5, sm: 2, md: 0 },
                }}
            >
                {STATUSES.map(({ key, label, accent }) => (
                    <Paper
                        key={key}
                        variant="outlined"
                        onDragOver={(e) => handleDragOver(e as unknown as DragEvent, key)}
                        onDragLeave={handleDragLeave}
                        onDrop={(e) => handleDrop(e as unknown as DragEvent, key)}
                        sx={{
                            flex: { xs: '0 0 80vw', sm: '0 0 280px', md: 1 },
                            maxWidth: { xs: '85vw', sm: 300, md: 'none' },
                            minWidth: 0,
                            p: 1.5,
                            scrollSnapAlign: { xs: 'start', md: 'none' },
                            bgcolor: dragOverStatus === key ? 'action.hover' : 'background.default',
                            transition: 'background-color 0.15s',
                            ...(accent ? { borderColor: accent, borderTopWidth: 3 } : {}),
                        }}
                    >
                        <Stack direction="row" spacing={1} sx={{ mb: 1.5, alignItems: 'center' }}>
                            <Typography variant="subtitle2" sx={{ fontWeight: 700, textTransform: 'uppercase', letterSpacing: 0.5, color: accent ?? 'inherit' }}>
                                {label}
                            </Typography>
                            <Chip label={ticketsByStatus(key).length} size="small" variant="outlined" />
                        </Stack>

                        <Stack spacing={1}>
                            {ticketsByStatus(key).map((ticket) => (
                                <Card
                                    key={ticket.id}
                                    variant="outlined"
                                    draggable={!isMobile}
                                    onDragStart={(e) => handleDragStart(e as unknown as DragEvent, ticket.id)}
                                    onDragEnd={handleDragEnd}
                                    onClick={() => handleTicketClick(ticket)}
                                    sx={{
                                        cursor: isMobile ? 'pointer' : 'grab',
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
                                        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 0.5 }}>
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
                                            {ticket.labels.map((label) => (
                                                <Chip
                                                    key={label}
                                                    label={label}
                                                    size="small"
                                                    variant="outlined"
                                                    sx={{
                                                        fontSize: '0.7rem',
                                                        borderColor: '#7b1fa2',
                                                        color: '#7b1fa2',
                                                    }}
                                                />
                                            ))}
                                            {ticket.assignee && (
                                                <Chip
                                                    label={ticket.assignee}
                                                    size="small"
                                                    variant="outlined"
                                                    sx={{ fontSize: '0.7rem' }}
                                                />
                                            )}
                                        </Stack>
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
                fullScreen={isMobile}
            >
                <DialogTitle>
                    <TextField
                        value={editTitle}
                        onChange={(e) => setEditTitle(e.target.value)}
                        variant="standard"
                        fullWidth
                        slotProps={{ input: { sx: { fontSize: '1.25rem', fontWeight: 600 } } }}
                    />
                    <Stack
                        direction={{ xs: 'column', sm: 'row' }}
                        spacing={{ xs: 1.5, sm: 2 }}
                        sx={{ mt: 1, alignItems: { xs: 'stretch', sm: 'center' }, flexWrap: 'wrap' }}
                    >
                        {editingTicket?.reporter && (
                            <Typography variant="caption" color="text.secondary">
                                Reporter: {editingTicket.reporter}
                            </Typography>
                        )}
                        <TextField
                            select
                            label="Status"
                            value={editStatus}
                            onChange={(e) => setEditStatus(e.target.value)}
                            size="small"
                            sx={{ minWidth: { xs: '100%', sm: 150 } }}
                        >
                            {STATUSES.map((s) => (
                                <MenuItem key={s.key} value={s.key}>{s.label}</MenuItem>
                            ))}
                        </TextField>
                        <TextField
                            select
                            label="Project"
                            value={editProjectId}
                            onChange={(e) => setEditProjectId(e.target.value)}
                            size="small"
                            sx={{ minWidth: { xs: '100%', sm: 150 } }}
                        >
                            {projects.map((p) => (
                                <MenuItem key={p.id} value={p.id}>{p.title}</MenuItem>
                            ))}
                        </TextField>
                        <TextField
                            select
                            label="Assignee"
                            value={editAssignee}
                            onChange={(e) => setEditAssignee(e.target.value)}
                            size="small"
                            sx={{ minWidth: { xs: '100%', sm: 150 } }}
                        >
                            <MenuItem value="">Unassigned</MenuItem>
                            {users.map((u) => (
                                <MenuItem key={u.id} value={u.id}>{u.name}</MenuItem>
                            ))}
                        </TextField>
                        <Autocomplete
                            multiple
                            size="small"
                            freeSolo
                            options={labels}
                            value={editLabels}
                            onChange={(_, val) => setEditLabels(val)}
                            renderInput={(params) => <TextField {...params} label="Labels" size="small" />}
                            sx={{ minWidth: { xs: '100%', sm: 200 }, flex: { sm: 1 } }}
                        />
                    </Stack>
                </DialogTitle>
                <DialogContent sx={{ p: 0, height: { xs: 'auto', sm: 450 }, flex: { xs: 1, sm: 'initial' }, minHeight: { xs: 300, sm: 450 } }}>
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
                    <Button onClick={() => setConfirmingDelete(true)} disabled={saving || improving} color="error">
                        Delete
                    </Button>
                    <Box sx={{ flex: 1 }} />
                    <Button onClick={handleEditorClose}>Cancel</Button>
                    <Button variant="contained" onClick={handleEditorSave} disabled={saving || improving}>
                        {saving ? 'Saving...' : 'Save'}
                    </Button>
                </DialogActions>
            </Dialog>

            <Dialog open={confirmingDelete} onClose={() => setConfirmingDelete(false)} maxWidth="xs">
                <DialogTitle>Delete Ticket?</DialogTitle>
                <DialogContent>
                    <Typography>
                        Are you sure you want to delete ticket <strong>{editingTicket?.id}</strong>: &quot;{editingTicket?.title}&quot;? This action cannot be undone from the UI.
                    </Typography>
                </DialogContent>
                <DialogActions>
                    <Button onClick={() => setConfirmingDelete(false)}>Cancel</Button>
                    <Button onClick={handleDeleteTicket} color="error" variant="contained" disabled={saving}>
                        {saving ? 'Deleting...' : 'Delete'}
                    </Button>
                </DialogActions>
            </Dialog>

            <Dialog
                open={creatingTicket}
                onClose={() => setCreatingTicket(false)}
                maxWidth="md"
                fullWidth
                fullScreen={isMobile}
            >
                <DialogTitle>New Ticket</DialogTitle>
                <DialogContent sx={{ display: 'flex', flexDirection: 'column', gap: 2, pt: '8px !important' }}>
                    <TextField
                        select
                        label="Project"
                        value={newTicketProjectId}
                        onChange={(e) => setNewTicketProjectId(e.target.value)}
                        fullWidth
                        size="small"
                    >
                        {projects.map((p) => (
                            <MenuItem key={p.id} value={p.id}>{p.title}</MenuItem>
                        ))}
                    </TextField>
                    <TextField
                        label="Title"
                        value={newTicketTitle}
                        onChange={(e) => setNewTicketTitle(e.target.value)}
                        fullWidth
                        autoFocus
                    />
                    <TextField
                        select
                        label="Assignee"
                        value={newTicketAssignee}
                        onChange={(e) => setNewTicketAssignee(e.target.value)}
                        fullWidth
                        size="small"
                    >
                        <MenuItem value="">Unassigned</MenuItem>
                        {users.map((u) => (
                            <MenuItem key={u.id} value={u.id}>{u.name}</MenuItem>
                        ))}
                    </TextField>
                    <Autocomplete
                        multiple
                        size="small"
                        freeSolo
                        options={labels}
                        value={newTicketLabels}
                        onChange={(_, val) => setNewTicketLabels(val)}
                        renderInput={(params) => <TextField {...params} label="Labels" size="small" />}
                        fullWidth
                    />
                    <Box sx={{ height: 300 }}>
                        <Editor
                            height="100%"
                            defaultLanguage="markdown"
                            value={newTicketDescription}
                            onChange={(v) => setNewTicketDescription(v ?? '')}
                            theme="vs-dark"
                            options={{ minimap: { enabled: false }, wordWrap: 'on', fontSize: 14, lineNumbers: 'off' }}
                        />
                    </Box>
                </DialogContent>
                <DialogActions>
                    <Button onClick={() => setCreatingTicket(false)}>Cancel</Button>
                    <Button
                        variant="contained"
                        onClick={handleCreateTicket}
                        disabled={savingNew || !newTicketTitle.trim() || !newTicketProjectId}
                    >
                        {savingNew ? 'Creating...' : 'Create'}
                    </Button>
                </DialogActions>
            </Dialog>

            {/* Manage Projects Dialog */}
            <Dialog open={managingProjects} onClose={() => setManagingProjects(false)} maxWidth="sm" fullWidth fullScreen={isMobile}>
                <DialogTitle>Manage Projects</DialogTitle>
                <DialogContent>
                    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ mb: 2, mt: 1 }}>
                        <TextField
                            label="New project title"
                            value={newProjectTitle}
                            onChange={(e) => setNewProjectTitle(e.target.value)}
                            size="small"
                            sx={{ flex: 1 }}
                        />
                        <TextField
                            label="Description (optional)"
                            value={newProjectDescription}
                            onChange={(e) => setNewProjectDescription(e.target.value)}
                            size="small"
                            sx={{ flex: 1 }}
                        />
                        <Button
                            variant="contained"
                            size="small"
                            disabled={!newProjectTitle.trim()}
                            onClick={async () => {
                                await createProject(newProjectTitle.trim(), newProjectDescription.trim() || undefined);
                                const projs = await getProjects();
                                setProjects(projs);
                                setNewProjectTitle('');
                                setNewProjectDescription('');
                            }}
                        >
                            Add
                        </Button>
                    </Stack>
                    <List dense>
                        {projects.map((p) => (
                            <ListItem key={p.id}>
                                <ListItemText primary={p.title} secondary={`${p.ticketCount} tickets`} />
                                <ListItemSecondaryAction>
                                    <IconButton
                                        edge="end"
                                        size="small"
                                        disabled={p.ticketCount > 0}
                                        title={p.ticketCount > 0 ? 'Cannot delete project with tickets' : 'Delete project'}
                                        onClick={async () => {
                                            await deleteProject(p.id);
                                            setProjects(prev => prev.filter(x => x.id !== p.id));
                                            setTickets(prev => prev.filter(t => t.projectId !== p.id));
                                        }}
                                    >
                                        <DeleteIcon fontSize="small" />
                                    </IconButton>
                                </ListItemSecondaryAction>
                            </ListItem>
                        ))}
                    </List>
                </DialogContent>
                <DialogActions>
                    <Button onClick={() => setManagingProjects(false)}>Close</Button>
                </DialogActions>
            </Dialog>

            {/* Manage Labels Dialog */}
            <Dialog open={managingLabels} onClose={() => setManagingLabels(false)} maxWidth="sm" fullWidth fullScreen={isMobile}>
                <DialogTitle>Manage Labels</DialogTitle>
                <DialogContent>
                    <Stack direction="row" spacing={1} sx={{ mb: 2, mt: 1 }}>
                        <TextField
                            label="New label"
                            value={newLabelName}
                            onChange={(e) => setNewLabelName(e.target.value)}
                            size="small"
                            sx={{ flex: 1 }}
                            onKeyDown={(e) => {
                                if (e.key === 'Enter' && newLabelName.trim()) {
                                    addLabel(newLabelName.trim()).then(setLabels);
                                    setNewLabelName('');
                                }
                            }}
                        />
                        <Button
                            variant="contained"
                            size="small"
                            disabled={!newLabelName.trim()}
                            onClick={async () => {
                                const updated = await addLabel(newLabelName.trim());
                                setLabels(updated);
                                setNewLabelName('');
                            }}
                        >
                            Add
                        </Button>
                    </Stack>
                    <List dense>
                        {labels.map((label) => (
                            <ListItem key={label}>
                                <ListItemText primary={label} />
                                <ListItemSecondaryAction>
                                    <IconButton
                                        edge="end"
                                        size="small"
                                        onClick={async () => {
                                            await removeLabel(label);
                                            setLabels(prev => prev.filter(l => l !== label));
                                        }}
                                    >
                                        <DeleteIcon fontSize="small" />
                                    </IconButton>
                                </ListItemSecondaryAction>
                            </ListItem>
                        ))}
                        {labels.length === 0 && (
                            <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                                No labels yet. Add one above.
                            </Typography>
                        )}
                    </List>
                </DialogContent>
                <DialogActions>
                    <Button onClick={() => setManagingLabels(false)}>Close</Button>
                </DialogActions>
            </Dialog>
        </Box>
    );
}
