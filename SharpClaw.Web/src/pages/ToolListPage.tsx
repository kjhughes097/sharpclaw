import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Paper from '@mui/material/Paper';
import { getTools } from '../api/tools';
import type { ToolSummary } from '../api/tools';

export default function ToolListPage() {
    const [tools, setTools] = useState<ToolSummary[]>([]);
    const navigate = useNavigate();

    useEffect(() => { getTools().then(setTools); }, []);

    return (
        <Box>
            <Typography variant="h4" gutterBottom>Tools</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                Tools are code-defined and read-only. Click a tool to view its parameters.
            </Typography>
            <TableContainer component={Paper} variant="outlined">
                <Table>
                    <TableHead>
                        <TableRow>
                            <TableCell>Name</TableCell>
                            <TableCell>Description</TableCell>
                            <TableCell>Parameters</TableCell>
                        </TableRow>
                    </TableHead>
                    <TableBody>
                        {tools.map((t) => (
                            <TableRow
                                key={t.name}
                                hover
                                sx={{ cursor: 'pointer' }}
                                onClick={() => navigate(`/tools/${t.name}`)}
                            >
                                <TableCell sx={{ fontWeight: 600, fontFamily: 'monospace' }}>{t.name}</TableCell>
                                <TableCell>{t.description}</TableCell>
                                <TableCell>{t.parameters.length}</TableCell>
                            </TableRow>
                        ))}
                    </TableBody>
                </Table>
            </TableContainer>
        </Box>
    );
}
