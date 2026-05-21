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
import AddIcon from '@mui/icons-material/Add';
import { getSkills } from '../api/skills';
import type { SkillSummary } from '../api/skills';

export default function SkillListPage() {
    const [skills, setSkills] = useState<SkillSummary[]>([]);
    const navigate = useNavigate();

    useEffect(() => { getSkills().then(setSkills); }, []);

    return (
        <Box>
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
                <Typography variant="h4">Skills</Typography>
                <Button variant="contained" startIcon={<AddIcon />} onClick={() => navigate('/skills/new')}>
                    New Skill
                </Button>
            </Box>
            <TableContainer component={Paper} variant="outlined">
                <Table>
                    <TableHead>
                        <TableRow>
                            <TableCell>Name</TableCell>
                            <TableCell>Description</TableCell>
                        </TableRow>
                    </TableHead>
                    <TableBody>
                        {skills.map((s) => (
                            <TableRow
                                key={s.name}
                                hover
                                sx={{ cursor: 'pointer' }}
                                onClick={() => navigate(`/skills/${s.name}`)}
                            >
                                <TableCell sx={{ fontWeight: 600 }}>{s.name}</TableCell>
                                <TableCell>{s.description ?? '—'}</TableCell>
                            </TableRow>
                        ))}
                    </TableBody>
                </Table>
            </TableContainer>
        </Box>
    );
}
