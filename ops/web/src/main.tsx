import { QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { queryClient } from '@/api/client';
import { Layout } from '@/components/layout';
import { FeedbackPage } from '@/pages/feedback';
import { NoticesPage } from '@/pages/notices';
import { OverviewPage } from '@/pages/overview';
import { RemindersPage } from '@/pages/reminders';
import { SystemPage } from '@/pages/system';
import './index.css';

const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { index: true, element: <OverviewPage /> },
      { path: 'reminders', element: <RemindersPage /> },
      { path: 'feedback', element: <FeedbackPage /> },
      { path: 'notices', element: <NoticesPage /> },
      { path: 'system', element: <SystemPage /> },
      { path: '*', element: <p>Page not found.</p> },
    ],
  },
]);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
