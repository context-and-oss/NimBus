import { Route, Routes } from 'react-router-dom';
import AppShell from './AppShell';
import { DemoPaneProvider } from './components/shell/demo-pane';
import { CrmDemoStateProvider } from './demo-state';
import AccountForm from './pages/AccountForm';
import AccountsList from './pages/AccountsList';
import ContactForm from './pages/ContactForm';
import ContactsList from './pages/ContactsList';

export default function App() {
  return (
    <CrmDemoStateProvider>
      <DemoPaneProvider>
        <AppShell>
          <Routes>
            <Route path="/" element={<AccountsList />} />
            <Route path="/accounts" element={<AccountsList />} />
            <Route path="/accounts/new" element={<AccountForm />} />
            <Route path="/accounts/:id" element={<AccountForm />} />
            <Route path="/contacts" element={<ContactsList />} />
            <Route path="/contacts/new" element={<ContactForm />} />
            <Route path="/contacts/:id" element={<ContactForm />} />
          </Routes>
        </AppShell>
      </DemoPaneProvider>
    </CrmDemoStateProvider>
  );
}
