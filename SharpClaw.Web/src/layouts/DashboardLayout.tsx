import { Outlet } from 'react-router-dom';
import Box from '@mui/material/Box';
import Stack from '@mui/material/Stack';
import SideMenu from '../components/SideMenu';
import AppNavbar from '../components/AppNavbar';

export default function DashboardLayout() {
    return (
        <Box sx={{ display: 'flex' }}>
            <SideMenu />
            <AppNavbar />
            <Box
                component="main"
                sx={{
                    flexGrow: 1,
                    overflow: 'auto',
                    minHeight: '100vh',
                }}
            >
                <Stack
                    spacing={2}
                    sx={{
                        mx: { xs: 1.5, sm: 2, md: 3 },
                        pb: 5,
                        mt: { xs: 9, md: 2 },
                    }}
                >
                    <Outlet />
                </Stack>
            </Box>
        </Box>
    );
}
