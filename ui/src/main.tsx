import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import { AppProviders, AppRoutes, createQueryClient } from './App';
import { createServices } from './app/services';
import { getTheme } from './lib/prefs';
import { applyTheme } from './lib/theme';
import './styles/index.css';

// Before the first render, so a light-theme user doesn't see a dark flash.
applyTheme(getTheme());

const queryClient = createQueryClient();
const services = createServices();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <AppProviders queryClient={queryClient} services={services}>
        <AppRoutes />
      </AppProviders>
    </BrowserRouter>
  </StrictMode>,
);
