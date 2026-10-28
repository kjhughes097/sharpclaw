import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import { AppProviders, AppRoutes, createQueryClient } from './App';
import { createServices } from './app/services';
import './styles/index.css';

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
