import { BrowserRouter, Route, Routes, NavLink } from "react-router-dom";
import { Repos } from "./pages/Repos";
import { RepoDetail } from "./pages/RepoDetail";
import { DbBrowser } from "./pages/DbBrowser";
import { Services } from "./pages/Services";
import { Settings } from "./pages/Settings";
import { Distributed } from "./pages/Distributed";
import { ApiProvider } from "./api/ApiContext";
import "./App.css";

/**
 * Top-level shell. Sidebar navigation + a content area; each page calls into the
 * local API via the ApiContext (which carries baseUrl + apiKey from runtime.json).
 */
function App() {
  return (
    <ApiProvider>
      <BrowserRouter>
        <div className="app-shell">
          <Sidebar />
          <main className="app-content">
            <Routes>
              <Route path="/" element={<Repos />} />
              <Route path="/repos/:repoId" element={<RepoDetail />} />
              <Route path="/db" element={<DbBrowser />} />
              <Route path="/services" element={<Services />} />
              <Route path="/distributed" element={<Distributed />} />
              <Route path="/settings" element={<Settings />} />
            </Routes>
          </main>
        </div>
      </BrowserRouter>
    </ApiProvider>
  );
}

function Sidebar() {
  return (
    <nav className="sidebar">
      <div className="sidebar-brand">AIMemory</div>
      <ul>
        <li><NavLink to="/" end>Repositories</NavLink></li>
        <li><NavLink to="/db">DB Browser</NavLink></li>
        <li><NavLink to="/services">Services</NavLink></li>
        <li><NavLink to="/distributed">Distributed</NavLink></li>
        <li><NavLink to="/settings">Settings</NavLink></li>
      </ul>
    </nav>
  );
}

export default App;
