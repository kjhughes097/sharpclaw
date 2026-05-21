import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import Paper from '@mui/material/Paper';
import Grid from '@mui/material/Grid';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Chip from '@mui/material/Chip';
import Alert from '@mui/material/Alert';
import TextField from '@mui/material/TextField';
import Select from '@mui/material/Select';
import MenuItem from '@mui/material/MenuItem';
import FormControl from '@mui/material/FormControl';
import InputLabel from '@mui/material/InputLabel';
import Switch from '@mui/material/Switch';
import FormControlLabel from '@mui/material/FormControlLabel';
import LinearProgress from '@mui/material/LinearProgress';
import CircularProgress from '@mui/material/CircularProgress';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import Avatar from '@mui/material/Avatar';
import Stack from '@mui/material/Stack';
import Divider from '@mui/material/Divider';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemText from '@mui/material/ListItemText';
import ListItemAvatar from '@mui/material/ListItemAvatar';
import DeleteIcon from '@mui/icons-material/Delete';
import EditIcon from '@mui/icons-material/Edit';
import StarIcon from '@mui/icons-material/Star';
import { LineChart } from '@mui/x-charts/LineChart';
import { BarChart } from '@mui/x-charts/BarChart';
import { PieChart } from '@mui/x-charts/PieChart';
import { DataGrid } from '@mui/x-data-grid';
import type { GridColDef } from '@mui/x-data-grid';
import { SimpleTreeView } from '@mui/x-tree-view/SimpleTreeView';
import { TreeItem } from '@mui/x-tree-view/TreeItem';

// -- Mock Data --
const lineData = {
    xAxis: [{ data: [1, 2, 3, 4, 5, 6, 7] }],
    series: [
        { data: [2, 5.5, 2, 8.5, 1.5, 5, 3], label: 'Series A' },
        { data: [3, 3.5, 6, 2, 7, 4, 5.5], label: 'Series B' },
    ],
};

const barData = {
    xAxis: [{ scaleType: 'band' as const, data: ['Mon', 'Tue', 'Wed', 'Thu', 'Fri'] }],
    series: [
        { data: [4, 3, 5, 2, 6], label: 'Tasks' },
        { data: [1, 6, 3, 8, 2], label: 'Completions' },
    ],
};

const pieData = [
    { id: 0, value: 35, label: 'Copilot' },
    { id: 1, value: 25, label: 'Anthropic' },
    { id: 2, value: 20, label: 'Scheduler' },
    { id: 3, value: 20, label: 'Manual' },
];

const gridColumns: GridColDef[] = [
    { field: 'id', headerName: 'ID', width: 70 },
    { field: 'name', headerName: 'Name', width: 150 },
    { field: 'role', headerName: 'Role', width: 130 },
    { field: 'status', headerName: 'Status', width: 100 },
    { field: 'score', headerName: 'Score', type: 'number', width: 90 },
];
const gridRows = [
    { id: 1, name: 'Ade', role: 'Orchestrator', status: 'Active', score: 95 },
    { id: 2, name: 'Cody', role: 'Engineer', status: 'Active', score: 92 },
    { id: 3, name: 'Fin', role: 'Finance', status: 'Active', score: 88 },
    { id: 4, name: 'Myles', role: 'Athletics', status: 'Idle', score: 85 },
    { id: 5, name: 'Deb', role: 'Debate', status: 'Idle', score: 90 },
];

const tableRows = [
    { name: 'spawn_agent', calls: 142, avgTime: '3.2s' },
    { name: 'execute_skill', calls: 89, avgTime: '1.8s' },
    { name: 'schedule_task', calls: 34, avgTime: '0.5s' },
    { name: 'workspace_read', calls: 256, avgTime: '0.1s' },
    { name: 'send_telegram', calls: 67, avgTime: '0.8s' },
];

export default function ExamplesPage() {
    return (
        <Box>
            <Typography variant="h4" gutterBottom>Component Examples</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
                A showcase of MUI and MUI X components with sample data, for reference when building pages.
            </Typography>

            <Grid container spacing={3}>
                {/* Line Chart */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Line Chart</Typography>
                        <LineChart
                            xAxis={lineData.xAxis}
                            series={lineData.series}
                            height={250}
                        />
                    </Paper>
                </Grid>

                {/* Bar Chart */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Bar Chart</Typography>
                        <BarChart
                            xAxis={barData.xAxis}
                            series={barData.series}
                            height={250}
                        />
                    </Paper>
                </Grid>

                {/* Pie Chart */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Pie Chart</Typography>
                        <PieChart
                            series={[{ data: pieData }]}
                            height={250}
                        />
                    </Paper>
                </Grid>

                {/* Data Grid */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Data Grid</Typography>
                        <DataGrid
                            rows={gridRows}
                            columns={gridColumns}
                            pageSizeOptions={[5]}
                            initialState={{ pagination: { paginationModel: { pageSize: 5 } } }}
                            disableRowSelectionOnClick
                            sx={{ height: 280 }}
                        />
                    </Paper>
                </Grid>

                {/* Table */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Table</Typography>
                        <TableContainer>
                            <Table size="small">
                                <TableHead>
                                    <TableRow>
                                        <TableCell>Tool</TableCell>
                                        <TableCell align="right">Calls</TableCell>
                                        <TableCell align="right">Avg Time</TableCell>
                                    </TableRow>
                                </TableHead>
                                <TableBody>
                                    {tableRows.map((row) => (
                                        <TableRow key={row.name}>
                                            <TableCell sx={{ fontFamily: 'monospace' }}>{row.name}</TableCell>
                                            <TableCell align="right">{row.calls}</TableCell>
                                            <TableCell align="right">{row.avgTime}</TableCell>
                                        </TableRow>
                                    ))}
                                </TableBody>
                            </Table>
                        </TableContainer>
                    </Paper>
                </Grid>

                {/* Tree View */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Tree View</Typography>
                        <SimpleTreeView>
                            <TreeItem itemId="agents" label="Agents">
                                <TreeItem itemId="ade" label="ade (Orchestrator)" />
                                <TreeItem itemId="cody" label="cody (Engineer)" />
                                <TreeItem itemId="fin" label="fin (Finance)" />
                            </TreeItem>
                            <TreeItem itemId="tools" label="Tools">
                                <TreeItem itemId="spawn_agent" label="spawn_agent" />
                                <TreeItem itemId="execute_skill" label="execute_skill" />
                                <TreeItem itemId="schedule_task" label="schedule_task" />
                            </TreeItem>
                            <TreeItem itemId="mcps" label="MCP Servers">
                                <TreeItem itemId="memory" label="memory (HTTP)" />
                                <TreeItem itemId="playwright" label="playwright (Stdio)" />
                            </TreeItem>
                        </SimpleTreeView>
                    </Paper>
                </Grid>

                {/* Cards */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Cards</Typography>
                        <Stack spacing={1}>
                            <Card variant="outlined">
                                <CardContent sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
                                    <Avatar sx={{ bgcolor: '#e91e63' }}>A</Avatar>
                                    <Box>
                                        <Typography variant="subtitle1">Agent Card</Typography>
                                        <Typography variant="body2" color="text.secondary">Card with avatar and content</Typography>
                                    </Box>
                                </CardContent>
                            </Card>
                            <Card variant="outlined">
                                <CardContent sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
                                    <Avatar sx={{ bgcolor: '#2196f3' }}>C</Avatar>
                                    <Box>
                                        <Typography variant="subtitle1">Another Card</Typography>
                                        <Typography variant="body2" color="text.secondary">Outlined variant</Typography>
                                    </Box>
                                </CardContent>
                            </Card>
                        </Stack>
                    </Paper>
                </Grid>

                {/* List */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>List</Typography>
                        <List dense>
                            <ListItem secondaryAction={<IconButton edge="end"><EditIcon /></IconButton>}>
                                <ListItemAvatar><Avatar><StarIcon /></Avatar></ListItemAvatar>
                                <ListItemText primary="List item with action" secondary="Secondary text" />
                            </ListItem>
                            <Divider />
                            <ListItem secondaryAction={<IconButton edge="end"><DeleteIcon /></IconButton>}>
                                <ListItemAvatar><Avatar><StarIcon /></Avatar></ListItemAvatar>
                                <ListItemText primary="Another item" secondary="With delete action" />
                            </ListItem>
                        </List>
                    </Paper>
                </Grid>

                {/* Buttons & Chips */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Buttons & Chips</Typography>
                        <Stack spacing={2}>
                            <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }} useFlexGap>
                                <Button variant="contained">Contained</Button>
                                <Button variant="outlined">Outlined</Button>
                                <Button variant="text">Text</Button>
                                <Button variant="contained" color="secondary">Secondary</Button>
                                <Button variant="contained" color="error">Error</Button>
                                <Button variant="contained" color="success">Success</Button>
                            </Stack>
                            <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }} useFlexGap>
                                <Chip label="Default" />
                                <Chip label="Primary" color="primary" />
                                <Chip label="Success" color="success" />
                                <Chip label="Warning" color="warning" />
                                <Chip label="Error" color="error" />
                                <Chip label="Deletable" onDelete={() => { }} />
                            </Stack>
                        </Stack>
                    </Paper>
                </Grid>

                {/* Form Inputs */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Form Inputs</Typography>
                        <Stack spacing={2}>
                            <TextField label="Text Field" size="small" defaultValue="Sample text" fullWidth />
                            <TextField label="Password" size="small" type="password" defaultValue="secret" fullWidth />
                            <FormControl size="small" fullWidth>
                                <InputLabel>Select</InputLabel>
                                <Select label="Select" defaultValue="copilot">
                                    <MenuItem value="copilot">Copilot</MenuItem>
                                    <MenuItem value="anthropic">Anthropic</MenuItem>
                                </Select>
                            </FormControl>
                            <FormControlLabel control={<Switch defaultChecked />} label="Toggle switch" />
                        </Stack>
                    </Paper>
                </Grid>

                {/* Alerts & Progress */}
                <Grid size={{ xs: 12, md: 6 }}>
                    <Paper variant="outlined" sx={{ p: 2 }}>
                        <Typography variant="h6" gutterBottom>Feedback</Typography>
                        <Stack spacing={2}>
                            <Alert severity="success">Operation completed successfully</Alert>
                            <Alert severity="info">Informational message</Alert>
                            <Alert severity="warning">Warning: check configuration</Alert>
                            <Alert severity="error">Error: connection failed</Alert>
                            <Box>
                                <Typography variant="body2" gutterBottom>Linear Progress</Typography>
                                <LinearProgress variant="determinate" value={65} />
                            </Box>
                            <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
                                <CircularProgress size={24} />
                                <Typography variant="body2">Loading...</Typography>
                            </Stack>
                        </Stack>
                    </Paper>
                </Grid>
            </Grid>
        </Box>
    );
}
