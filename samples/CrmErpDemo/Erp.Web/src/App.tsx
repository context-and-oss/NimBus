import { Route, Routes } from 'react-router-dom';
import AppShell from './AppShell';
import { DemoPaneProvider } from './components/shell/demo-pane';
import { ErpDemoStateProvider } from './demo-state';
import AlertsPage from './pages/AlertsPage';
import ContactForm from './pages/ContactForm';
import ContactsList from './pages/ContactsList';
import CustomerForm from './pages/CustomerForm';
import CustomersList from './pages/CustomersList';
import Home from './pages/Home';

export default function App() {
  return (
    <ErpDemoStateProvider>
      <DemoPaneProvider>
        <AppShell>
          <Routes>
            <Route path="/" element={<Home />} />
            <Route path="/customers" element={<CustomersList />} />
            <Route path="/customers/new" element={<CustomerForm />} />
            <Route path="/customers/:id" element={<CustomerForm />} />
            <Route path="/contacts" element={<ContactsList />} />
            <Route path="/contacts/new" element={<ContactForm />} />
            <Route path="/contacts/:id" element={<ContactForm />} />
            <Route path="/alerts" element={<AlertsPage />} />
          </Routes>
        </AppShell>
      </DemoPaneProvider>
    </ErpDemoStateProvider>
  );
}
