import { useLocation, useNavigate } from 'react-router-dom';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemButton from '@mui/material/ListItemButton';
import ListItemIcon from '@mui/material/ListItemIcon';
import ListItemText from '@mui/material/ListItemText';
import Divider from '@mui/material/Divider';
import Stack from '@mui/material/Stack';
import HomeRoundedIcon from '@mui/icons-material/HomeRounded';
import SmartToyRoundedIcon from '@mui/icons-material/SmartToyRounded';
import HubRoundedIcon from '@mui/icons-material/HubRounded';
import BuildRoundedIcon from '@mui/icons-material/BuildRounded';
import SchoolRoundedIcon from '@mui/icons-material/SchoolRounded';
import WidgetsRoundedIcon from '@mui/icons-material/WidgetsRounded';
import SettingsRoundedIcon from '@mui/icons-material/SettingsRounded';

const mainItems = [
    { text: 'Home', icon: <HomeRoundedIcon />, path: '/' },
    { text: 'Agents', icon: <SmartToyRoundedIcon />, path: '/agents' },
    { text: 'MCPs', icon: <HubRoundedIcon />, path: '/mcps' },
    { text: 'Tools', icon: <BuildRoundedIcon />, path: '/tools' },
    { text: 'Skills', icon: <SchoolRoundedIcon />, path: '/skills' },
    { text: 'Examples', icon: <WidgetsRoundedIcon />, path: '/examples' },
];

const secondaryItems = [
    { text: 'Config', icon: <SettingsRoundedIcon />, path: '/config' },
];

export default function MenuContent() {
    const location = useLocation();
    const navigate = useNavigate();

    const isSelected = (path: string) => {
        if (path === '/') return location.pathname === '/';
        return location.pathname.startsWith(path);
    };

    return (
        <Stack sx={{ flexGrow: 1, p: 1, justifyContent: 'space-between' }}>
            <List dense>
                {mainItems.map((item) => (
                    <ListItem key={item.text} disablePadding sx={{ display: 'block' }}>
                        <ListItemButton
                            selected={isSelected(item.path)}
                            onClick={() => navigate(item.path)}
                        >
                            <ListItemIcon>{item.icon}</ListItemIcon>
                            <ListItemText primary={item.text} />
                        </ListItemButton>
                    </ListItem>
                ))}
            </List>
            <div>
                <Divider sx={{ my: 1 }} />
                <List dense>
                    {secondaryItems.map((item) => (
                        <ListItem key={item.text} disablePadding sx={{ display: 'block' }}>
                            <ListItemButton
                                selected={isSelected(item.path)}
                                onClick={() => navigate(item.path)}
                            >
                                <ListItemIcon>{item.icon}</ListItemIcon>
                                <ListItemText primary={item.text} />
                            </ListItemButton>
                        </ListItem>
                    ))}
                </List>
            </div>
        </Stack>
    );
}
