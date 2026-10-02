import { FormEvent, ReactNode, useEffect, useState } from "react";
import {
  Link,
  Navigate,
  Route,
  Routes,
  useLocation,
  useNavigate,
  useSearchParams,
} from "react-router-dom";
import { api, AuthProfile, tokenStore } from "./api";
import { ApiKeysPage } from "./ApiKeysPage";
import { OrganizationSetupPage } from "./OrganizationSetupPage";
import { MarketingHome } from "./MarketingHome";
import { SolutionsPage } from "./SolutionsPage";
import { ApiDevelopersPage } from "./ApiDevelopersPage";
import { AboutPage } from "./AboutPage";
import { VerifyPage } from "./VerifyPage";
import { VerificationHistoryPage } from "./VerificationHistoryPage";
import { TeamPage } from "./TeamPage";
import { AcceptInvite, Login, Register } from "./AuthScreens";
import { PricingPage } from "./PricingPage";
import { useEnvironment } from "./useEnvironment";

type Json = Record<string, unknown>;
function Field({
  label,
  ...props
}: { label: string } & React.InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className="field">
      <span>{label}</span>
      <input {...props} />
    </label>
  );
}
function Button({
  children,
  ...props
}: React.ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button className="button button-primary" {...props}>
      {children}
    </button>
  );
}
function Notice({
  message,
  error = false,
}: {
  message: string;
  error?: boolean;
}) {
  return message ? (
    <div
      className={`notice ${error ? "error" : "success"}`}
      role={error ? "alert" : "status"}
    >
      {message}
    </div>
  ) : null;
}
function PageTitle({
  eyebrow,
  title,
  children,
}: {
  eyebrow: string;
  title: string;
  children?: ReactNode;
}) {
  return (
    <div className="page-title">
      <div className="eyebrow">{eyebrow}</div>
      <h1>{title}</h1>
      {children}
    </div>
  );
}
function AuthFrame({
  children,
  title,
  eyebrow = "IDENTITY OPERATIONS PLATFORM",
}: {
  children: ReactNode;
  title: ReactNode;
  eyebrow?: string;
}) {
  return (
    <div className="auth-page">
      <div className="auth-panel">
        <Link className="brand" to="/">
          <span className="brand-mark">T</span>
          <span>
            Truvo<span className="accent">ID</span>
          </span>
        </Link>
        <div className="auth-copy">
          <div className="eyebrow">{eyebrow}</div>
          <h1>{title}</h1>
          <p>One trusted layer for every identity decision.</p>
        </div>
        {children}
      </div>
      <div className="auth-visual">
        <div className="grid-glow" />
        <div className="visual-copy">
          <span className="eyebrow">TRUST LAYER / 001</span>
          <strong>
            Every signal.
            <br />
            One clear answer.
          </strong>
        </div>
      </div>
    </div>
  );
}
function Home() {
  return <MarketingHome />;
}
function Shell({
  profile,
  onLogout,
}: {
  profile: AuthProfile;
  onLogout: () => void;
}) {
  const location = useLocation();
  const isAdmin = profile.role.toLowerCase().includes("platform");
  const environment = useEnvironment();
  const items = [
    ["/dashboard", "Overview"],
    ["/setup", "Organization setup"],
    ["/verify", "Verify identity"],
    ["/history", "History"],
    ["/team", "Team"],
    ["/outlets", "Outlets"],
    ["/api-keys", "API keys"],
    ["/wallet", "Wallet"],
  ];
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <Link className="brand" to="/dashboard">
          <span className="brand-mark">T</span>
          <span>
            Truvo<span className="accent">ID</span>
          </span>
        </Link>
        <div className="workspace-label">WORKSPACE</div>
        <div className="workspace">
          <span className="workspace-dot" />
          {profile.institutionName || "Platform operations"}
        </div>
        <nav>
          {items.map(([path, label], index) => (
            <Link
              className={
                location.pathname === path ? "nav-link active" : "nav-link"
              }
              key={path}
              to={path}
            >
              <span className="nav-index">
                {String(index + 1).padStart(2, "0")}
              </span>
              {label}
            </Link>
          ))}
        </nav>
        {isAdmin && (
          <>
            <div className="workspace-label admin-label">ADMIN</div>
            <nav>
              {[
                ["/admin/agencies", "A", "Organizations"],
                ["/admin/pricing", "P", "Pricing"],
              ].map(([path, index, label]) => (
                <Link
                  key={path}
                  className={location.pathname === path ? "nav-link admin-active" : "nav-link"}
                  to={path}
                >
                  <span className="nav-index">{index}</span>
                  {label}
                </Link>
              ))}
            </nav>
          </>
        )}
        <div className="sidebar-footer">
          <div className="status">
            <span /> API operational
          </div>
          <button className="sign-out" onClick={onLogout}>
            Sign out
          </button>
        </div>
      </aside>
      <main className="main-content">
        <header className="topbar">
          <div className="mobile-brand">
            Truvo<span className="accent">ID</span>
          </div>
          <div className="topbar-actions">
            <span className="eyebrow">{profile.fullName || profile.email}</span>
            <div className="avatar">
              {(profile.fullName || profile.email)[0].toUpperCase()}
            </div>
          </div>
        </header>
        {environment === "sandbox" && (
          <div className="sandbox-banner" role="note">
            <strong>SANDBOX</strong> Test environment — no real identity lookups or payments.
            Use the documented test numbers and free test funds.
          </div>
        )}
        <div className="page-content">
          <Routes>
            <Route
              path="/dashboard"
              element={<Dashboard profile={profile} />}
            />
            <Route path="/setup" element={<OrganizationSetupPage />} />
            <Route path="/verify" element={<VerifyPage />} />
            <Route path="/history" element={<VerificationHistoryPage />} />
            <Route path="/team" element={<TeamPage profile={profile} />} />
            <Route path="/outlets" element={<Outlets />} />
            <Route
              path="/api-keys"
              element={<ApiKeysPage profile={profile} />}
            />
            <Route path="/wallet" element={<Wallet />} />
            {isAdmin && (
              <Route path="/admin/agencies" element={<InviteAgency />} />
            )}
            {isAdmin && <Route path="/admin/pricing" element={<PricingPage />} />}
            <Route path="*" element={<Navigate to="/dashboard" replace />} />
          </Routes>
        </div>
      </main>
    </div>
  );
}
function Dashboard({ profile }: { profile: AuthProfile }) {
  const [setup, setSetup] = useState<Json | null>(null);
  useEffect(() => {
    if (!profile.outletId)
      api
        .get<Json>("/v1/tenant/setup")
        .then(setSetup)
        .catch(() => undefined);
  }, [profile.outletId]);
  const progress = Number(setup?.progress ?? 0);
  if (profile.outletId)
    return (
      <section>
        <PageTitle eyebrow="OUTLET WORKSPACE" title="Your outlet is ready.">
          <p className="lede">
            Run scoped verifications, monitor outlet activity, and manage the
            credentials assigned to this outlet.
          </p>
        </PageTitle>
        <div className="activity-card">
          <div>
            <div className="eyebrow">OUTLET OPERATIONS</div>
            <h2>Keep every decision close to the work.</h2>
            <p>
              This workspace is limited to your outlet scope. Sibling outlets
              and agency controls remain private.
            </p>
          </div>
          <Link className="button button-primary" to="/verify">
            Run verification ↗
          </Link>
        </div>
      </section>
    );
  return (
    <section>
      <PageTitle eyebrow="TENANT WORKSPACE" title="Welcome to TruvoID.">
        <p className="lede">
          Your workspace is ready. Complete the organization profile
          progressively, then configure operations when you are ready.
        </p>
      </PageTitle>
      <div className="activity-card">
        <div>
          <div className="eyebrow">ORGANIZATION SETUP</div>
          <h2>
            {progress === 100
              ? "Your organization profile is ready."
              : `${progress}% of your profile is complete.`}
          </h2>
          <p>
            Save information section by section. Outlets, API keys, and wallet
            funding remain optional until operations begin.
          </p>
        </div>
        <Link className="button button-primary" to="/setup">
          {progress === 100 ? "Review setup ↗" : "Continue setup ↗"}
        </Link>
      </div>
      {profile.role.toLowerCase().includes("agency") && (
        <div className="activity-card">
          <div>
            <div className="eyebrow">AGENCY OPERATIONS</div>
            <h2>Build your outlet network.</h2>
            <p>
              Create outlets and issue credentials after your agency profile is
              ready.
            </p>
          </div>
          <Link className="button button-primary" to="/outlets">
            Manage outlets ↗
          </Link>
        </div>
      )}
    </section>
  );
}
function Outlets() {
  const [items, setItems] = useState<Json[]>([]);
  const [name, setName] = useState("");
  const [message, setMessage] = useState("");
  async function load() {
    try {
      setItems(await api.get<Json[]>("/v1/tenant/outlets"));
    } catch {
      setItems([]);
    }
  }
  useEffect(() => {
    void load();
  }, []);
  async function create(event: FormEvent) {
    event.preventDefault();
    try {
      await api.post("/v1/tenant/outlets", { name });
      setName("");
      setMessage("Outlet created.");
      await load();
    } catch (error) {
      setMessage(
        error instanceof Error ? error.message : "Could not create outlet.",
      );
    }
  }
  return (
    <section>
      <PageTitle eyebrow="AGENCY / OUTLETS" title="Your outlets.">
        <p className="lede">
          Create outlets after your organization profile is ready.
        </p>
      </PageTitle>
      <div className="form-card inline-form">
        <form onSubmit={create}>
          <Field
            label="Outlet name"
            required
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="Lagos branch"
          />
          <Button>Create outlet ↗</Button>
        </form>
        <Notice message={message} error={message.startsWith("Could")} />
      </div>
      {items.length ? (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Status</th>
                <th>Wallet</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <tr key={String(item.id)}>
                  <td>{String(item.name)}</td>
                  <td>{String(item.status)}</td>
                  <td>{String(item.walletId)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="empty">No outlets yet.</div>
      )}
    </section>
  );
}
function Wallet() {
  const [balance, setBalance] = useState<Json>({});
  const [ledger, setLedger] = useState<Json[]>([]);
  const environment = useEnvironment();
  const [funding, setFunding] = useState(false);
  const [fundMessage, setFundMessage] = useState<{ text: string; error?: boolean } | null>(null);
  function load() {
    Promise.all([
      api.get<Json>("/v1/tenant/wallet/balance"),
      api.get<Json[]>("/v1/tenant/wallet/ledger?page=1&pageSize=20"),
    ])
      .then(([wallet, entries]) => {
        setBalance(wallet);
        setLedger(entries);
      })
      .catch(() => undefined);
  }
  useEffect(load, []);
  async function addTestFunds() {
    setFunding(true);
    setFundMessage(null);
    try {
      await api.post("/v1/tenant/wallet/sandbox-funds", { amountKobo: 1_000_000 });
      setFundMessage({ text: "₦10,000.00 of test funds added." });
      load();
    } catch (error) {
      setFundMessage({ text: error instanceof Error ? error.message : "Test funds could not be added.", error: true });
    } finally {
      setFunding(false);
    }
  }
  return (
    <section>
      <PageTitle eyebrow="TENANT / WALLET" title="Wallet.">
        <p className="lede">
          Funding is optional during onboarding and available here when
          operations are ready.
        </p>
      </PageTitle>
      <div className="wallet-hero">
        <div>
          <span className="stat-label">AVAILABLE BALANCE</span>
          <strong>
            ₦
            {(Number(balance.balanceKobo ?? 0) / 100).toLocaleString("en-NG", {
              minimumFractionDigits: 2,
            })}
          </strong>
          <span className="stat-note">
            {environment === "sandbox" ? "Sandbox test balance — not real money" : "Tenant wallet balance"}
          </span>
        </div>
        {environment === "sandbox" && (
          <button className="button button-primary" onClick={() => void addTestFunds()} disabled={funding}>
            {funding ? <><span className="spinner" aria-hidden="true" />Adding…</> : "Add ₦10,000 test funds"}
          </button>
        )}
      </div>
      {fundMessage && (
        <div className={`notice ${fundMessage.error ? "error" : "success"}`} role="status">{fundMessage.text}</div>
      )}
      <div className="section-heading">
        <div>
          <div className="eyebrow">LEDGER</div>
          <h2>Recent wallet activity</h2>
        </div>
      </div>
      {ledger.length ? (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Type</th>
                <th>Amount</th>
                <th>Balance after</th>
                <th>Date</th>
              </tr>
            </thead>
            <tbody>
              {ledger.map((entry, index) => (
                <tr key={String(entry.id ?? index)}>
                  <td>
                    {String(entry.entryType ?? entry.type ?? "transaction")}
                  </td>
                  <td>
                    ₦
                    {(Number(entry.amountKobo ?? 0) / 100).toLocaleString(
                      "en-NG",
                      { minimumFractionDigits: 2 },
                    )}
                  </td>
                  <td>
                    ₦
                    {(Number(entry.balanceAfterKobo ?? 0) / 100).toLocaleString(
                      "en-NG",
                      { minimumFractionDigits: 2 },
                    )}
                  </td>
                  <td>
                    {entry.createdAt
                      ? new Date(String(entry.createdAt)).toLocaleString()
                      : "—"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="empty">No wallet activity yet.</div>
      )}
    </section>
  );
}
function InviteAgency() {
  const [form, setForm] = useState({
    agencyName: "",
    adminFullName: "",
    adminEmail: "",
  });
  const [invite, setInvite] = useState("");
  const [message, setMessage] = useState("");
  const [organizations, setOrganizations] = useState<Json[]>([]);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    api
      .get<Json[]>("/v1/admin/organizations")
      .then(setOrganizations)
      .catch(() => undefined);
  }, [refresh]);
  async function submit(event: FormEvent) {
    event.preventDefault();
    try {
      const result = await api.post<Json>("/v1/admin/agencies/invite", form);
      setInvite(
        `${window.location.origin}/accept-agency-invite?token=${String(result.invitationToken)}`,
      );
      setMessage(
        "Invitation created. Copy the link and send it to the agency administrator.",
      );
      setRefresh((value) => value + 1);
    } catch (error) {
      setMessage(
        error instanceof Error ? error.message : "Could not create invitation.",
      );
    }
  }
  async function changeStatus(id: string, action: string) {
    await api.post(`/v1/admin/organizations/${id}/${action}`, {});
    setRefresh((value) => value + 1);
  }
  return (
    <section>
      <PageTitle eyebrow="PLATFORM / AGENCIES" title="Organizations.">
        <p className="lede">
          Invite agencies and monitor every tenant workspace from one place.
        </p>
      </PageTitle>
      <div className="form-card narrow">
        <div className="eyebrow">INVITE AGENCY</div>
        <form onSubmit={submit}>
          <Field
            label="Agency name"
            required
            value={form.agencyName}
            onChange={(event) =>
              setForm({ ...form, agencyName: event.target.value })
            }
          />
          <Field
            label="Administrator name"
            required
            value={form.adminFullName}
            onChange={(event) =>
              setForm({ ...form, adminFullName: event.target.value })
            }
          />
          <Field
            label="Administrator email"
            required
            type="email"
            value={form.adminEmail}
            onChange={(event) =>
              setForm({ ...form, adminEmail: event.target.value })
            }
          />
          <Button>Generate invitation ↗</Button>
        </form>
        <Notice message={message} error={message.startsWith("Could")} />
        {invite && (
          <div className="key-reveal">
            <span>Invitation link</span>
            <code>{invite}</code>
          </div>
        )}
      </div>
      {organizations.length ? (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Organization</th>
                <th>Type</th>
                <th>Status</th>
                <th>Setup</th>
                <th>Users</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {organizations.map((item) => (
                <tr key={String(item.id)}>
                  <td>{String(item.name)}</td>
                  <td>{String(item.type)}</td>
                  <td>
                    <span className={`badge ${String(item.status)}`}>
                      {String(item.status)}
                    </span>
                  </td>
                  <td>{String(item.setupStatus)}</td>
                  <td>{String(item.userCount)}</td>
                  <td>
                    {String(item.status) === "suspended" ? (
                      <button
                        className="link-button"
                        onClick={() =>
                          void changeStatus(String(item.id), "reactivate")
                        }
                      >
                        Reactivate
                      </button>
                    ) : (
                      <button
                        className="link-button"
                        onClick={() =>
                          void changeStatus(String(item.id), "suspend")
                        }
                      >
                        Suspend
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="empty">No organizations found.</div>
      )}
    </section>
  );
}
export function App() {
  const [profile, setProfile] = useState<AuthProfile | null>(null);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    if (!tokenStore.accessToken) {
      setLoading(false);
      return;
    }
    api
      .profile()
      .then(setProfile)
      .catch(() => tokenStore.clear())
      .finally(() => setLoading(false));
  }, []);
  if (loading)
    return (
      <div className="loading-screen">
        <span className="brand-mark">T</span>
        <span className="pulse" />
      </div>
    );
  if (profile)
    return (
      <Shell
        profile={profile}
        onLogout={() => {
          tokenStore.clear();
          setProfile(null);
        }}
      />
    );
  return (
    <Routes>
      <Route path="/" element={<Home />} />
      <Route path="/solutions" element={<SolutionsPage />} />
      <Route path="/api-and-developers" element={<ApiDevelopersPage />} />
      <Route path="/about" element={<AboutPage />} />
      <Route path="/login" element={<Login onLogin={setProfile} Frame={AuthFrame} />} />
      <Route path="/register" element={<Register onLogin={setProfile} Frame={AuthFrame} />} />
      <Route path="/accept-agency-invite" element={<AcceptInvite kind="agency" Frame={AuthFrame} />} />
      <Route path="/accept-team-invite" element={<AcceptInvite kind="team" Frame={AuthFrame} />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
