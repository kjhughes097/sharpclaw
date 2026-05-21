import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import Paper from '@mui/material/Paper';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Chip from '@mui/material/Chip';
import { getTool } from '../api/tools';
import type { ToolSummary } from '../api/tools';

export default function ToolDetailPage() {
    const { name } = useParams<{ name: string }>();
    const [tool, setTool] = useState<ToolSummary | null>(null);

    useEffect(() => {
        if (name) getTool(name).then(setTool);
    }, [name]);

    if (!tool) return <Typography>Loading...</Typography>;

    return (
        <Box>
            <Typography variant="h4" gutterBottom sx={{ fontFamily: 'monospace' }}>{tool.name}</Typography>
            <Typography variant="body1" sx={{ mb: 3 }}>{tool.description}</Typography>
            <Typography variant="h6" gutterBottom>Parameters</Typography>
            <TableContainer component={Paper} variant="outlined">
                <Table size="small">
                    <TableHead>
                        <TableRow>
                            <TableCell>Name</TableCell>
                            <TableCell>Type</TableCell>
                            <TableCell>Required</TableCell>
                            <TableCell>Description</TableCell>
                        </TableRow>
                    </TableHead>
                    <TableBody>
                        {tool.parameters.map((p) => (
                            <TableRow key={p.name}>
                                <TableCell sx={{ fontFamily: 'monospace' }}>{p.name}</TableCell>
                                <TableCell><Chip label={p.type} size="small" /></TableCell>
                                <TableCell>{p.required ? 'Yes' : 'No'}</TableCell>
                                <TableCell>{p.description}</TableCell>
                            </TableRow>
                        ))}
                    </TableBody>
                </Table>
            </TableContainer>
        </Box>
    );
}
