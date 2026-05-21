import { useState } from 'react';
import { styled } from '@mui/material/styles';
import AppBar from '@mui/material/AppBar';
import Box from '@mui/material/Box';
import Stack from '@mui/material/Stack';
import MuiToolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import IconButton from '@mui/material/IconButton';
import MenuRoundedIcon from '@mui/icons-material/MenuRounded';
import Drawer from '@mui/material/Drawer';
import MenuContent from './MenuContent';

const Toolbar = styled(MuiToolbar)({
    width: '100%',
    padding: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
});

export default function AppNavbar() {
    const [open, setOpen] = useState(false);

    return (
        <AppBar
            position="fixed"
            sx={{
                display: { xs: 'auto', md: 'none' },
                boxShadow: 0,
                bgcolor: 'background.paper',
                backgroundImage: 'none',
                borderBottom: '1px solid',
                borderColor: 'divider',
            }}
        >
            <Toolbar variant="regular">
                <Stack direction="row" sx={{ alignItems: 'center', gap: 1 }}>
                    <Box
                        sx={{
                            width: 28,
                            height: 28,
                            borderRadius: '50%',
                            background: 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)',
                            display: 'flex',
                            alignItems: 'center',
                            justifyContent: 'center',
                            color: 'white',
                            fontWeight: 'bold',
                            fontSize: '0.65rem',
                        }}
                    >
                        SC
                    </Box>
                    <Typography variant="h6" sx={{ color: 'text.primary' }}>
                        SharpClaw
                    </Typography>
                </Stack>
                <IconButton aria-label="menu" onClick={() => setOpen(true)}>
                    <MenuRoundedIcon />
                </IconButton>
            </Toolbar>
            <Drawer
                anchor="left"
                open={open}
                onClose={() => setOpen(false)}
                sx={{ display: { xs: 'block', md: 'none' } }}
            >
                <Box sx={{ width: 240, p: 1 }} onClick={() => setOpen(false)}>
                    <MenuContent />
                </Box>
            </Drawer>
        </AppBar>
    );
}
