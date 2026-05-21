import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Paper from '@mui/material/Paper';
import Chip from '@mui/material/Chip';
import AddIcon from '@mui/icons-material/Add';
import { getMcps } from '../api/mcps';
import type { McpSummary } from '../api/mcps';

export default function McpListPage() {
    const [mcps, setMcps] = useState<McpSummary[]>([]);
    const navigate = useNavigate();

    useEffect(() => { getMcps().then(setMcps); }, []);

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
                <Typography variant="h4">MCP Servers</Typography>
                <Button variant="contained" startIcon={<AddIcon />} onClick={() => navigate('/mcps/new')}>
                    New MCP
                </Button>
            </Box>
            <TableContainer component={Paper} variant="outlined">
                <Table>
                    <TableHead>
                        <TableRow>
                            <TableCell>Name</TableCell>
                            <TableCell>Transport</TableCell>
                            <TableCell>Command / URL</TableCell>
                        </TableRow>
                    </TableHead>
                    <TableBody>
                        {mcps.map((m) => (
                            <TableRow
                                key={m.name}
                                hover
                                sx={{ cursor: 'pointer' }}
                                onClick={() => navigate(`/mcps/${m.name}`)}
                            >
                                <TableCell sx={{ fontWeight: 600 }}>{m.name}</TableCell>
                                <TableCell><Chip label={m.transport} size="small" /></TableCell>
                                <TableCell>{m.command ?? m.url ?? '—'}</TableCell>
                            </TableRow>
                        ))}
                    </TableBody>
                </Table>
            </TableContainer>
        </Box>
    );
}
