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
import { api, AuthProfile, SESSION_EXPIRED_EVENT, tokenStore } from "./api";
import { ApiKeysPage } from "./ApiKeysPage";
import { OrganizationSetupPage, PROFILE_CHANGED_EVENT } from "./OrganizationSetupPage";
import { MarketingHome } from "./MarketingHome";
import { SolutionsPage } from "./SolutionsPage";
import { ApiDevelopersPage } from "./ApiDevelopersPage";
import { AboutPage } from "./AboutPage";
import { VerifyPage } from "./VerifyPage";
import { VerificationHistoryPage } from "./VerificationHistoryPage";
import { TeamPage } from "./TeamPage";
import { AcceptInvite, AdminLogin, ForgotPassword, Login, Register, ResetPassword } from "./AuthScreens";
import { PricingPage } from "./PricingPage";
import { ApiKeysAdminPage } from "./ApiKeysAdminPage";
import { OutletDetailPage } from "./OutletDetailPage";
import { AdminReview } from "./AdminReview";
import { CopyButton } from "./CopyButton";
import { useEnvironment } from "./useEnvironment";
import { useMode } from "./mode";

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
function Brand({ to, size = 28 }: { to: string; size?: number }) {
  return (
    <Link className="brand" to={to}>
      <img className="brand-logo" src="/TruvoID-logo.png" alt="TruvoID" width={size} height={size} />
      <span>
        Truvo<span className="accent">ID</span>
      </span>
    </Link>
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
        <Brand to="/" />
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
  const isAdmin = profile.tenantRole === "platform_admin" || profile.role.toLowerCase().includes("platform");
  const isOutlet = Boolean(profile.outletId);
  const environment = useEnvironment();
  const liveEnabled = Boolean(profile.liveEnabled);
  const [mode, changeMode] = useMode(liveEnabled);
  // Platform staff run TruvoID; they don't have a workspace of their own. Outlet users
  // get only the outlet-scoped surfaces — setup, team, outlets, and org API keys are
  // organization-administrator tools the API would reject anyway.
  const items = isAdmin
    ? [
        ["/admin/agencies", "Organizations"],
        ["/admin/pricing", "Pricing"],
        ["/admin/api-keys", "API keys"],
      ]
    : isOutlet
      ? [
          ["/dashboard", "Overview"],
          ["/verify", "Verify identity"],
          ["/history", "History"],
          ["/wallet", "Wallet"],
        ]
      : [
          ["/dashboard", "Overview"],
          ["/setup", "Organization setup"],
          ["/verify", "Verify identity"],
          ["/history", "History"],
          ["/team", "Team"],
          ["/outlets", "Outlets"],
          ["/api-keys", "API keys"],
          ["/wallet", "Wallet"],
        ];
  const goLiveHint =
    profile.setupStatus === "submitted"
      ? "Your profile is under review — live unlocks once TruvoID approves it."
      : profile.setupStatus === "needs_changes"
        ? "TruvoID asked for changes to your profile before you can go live."
        : "Complete and submit your organization profile to go live.";
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <Brand to={isAdmin ? "/admin/agencies" : "/dashboard"} />
        <div className="workspace-label">{isAdmin ? "OPERATIONS" : "WORKSPACE"}</div>
        <div className="workspace">
          <span className="workspace-dot" />
          {isAdmin ? "TruvoID platform" : profile.institutionName}
        </div>
        <nav>
          {items.map(([path, label], index) => (
            <Link
              className={location.pathname === path ? "nav-link active" : "nav-link"}
              key={path}
              to={path}
            >
              <span className="nav-index">{String(index + 1).padStart(2, "0")}</span>
              {label}
            </Link>
          ))}
        </nav>
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
            <img className="brand-logo" src="/TruvoID-logo.png" alt="TruvoID" width={24} height={24} />
            <span>Truvo<span className="accent">ID</span></span>
          </div>
          <div className="topbar-actions">
            {!isAdmin && environment !== "sandbox" && (
              <div className="mode-switch" role="radiogroup" aria-label="Verification mode">
                <button type="button" role="radio" aria-checked={mode === "test"}
                  className={mode === "test" ? "active test" : ""} onClick={() => changeMode("test")}>
                  Test
                </button>
                <button type="button" role="radio" aria-checked={mode === "live"} disabled={!liveEnabled}
                  title={liveEnabled ? "Live verifications are charged to your wallet" : goLiveHint}
                  className={mode === "live" ? "active live" : ""} onClick={() => changeMode("live")}>
                  Live
                </button>
              </div>
            )}
            <span className="eyebrow">{profile.fullName || profile.email}</span>
            <div className="avatar">{(profile.fullName || profile.email)[0].toUpperCase()}</div>
          </div>
        </header>
        {environment === "sandbox" ? (
          <div className="sandbox-banner" role="note">
            <strong>SANDBOX</strong> Test environment — no real identity lookups or payments.
          </div>
        ) : !isAdmin && mode === "test" ? (
          <div className="sandbox-banner" role="note">
            <strong>TEST MODE</strong> Verifications are free and use test numbers — nothing real is looked up.
            {!liveEnabled && (
              <>
                {" "}{goLiveHint}{" "}
                {profile.setupStatus !== "submitted" && <Link to="/setup">Continue setup →</Link>}
              </>
            )}
          </div>
        ) : null}
        <div className="page-content">
          <Routes>
            {isAdmin ? (
              <>
                <Route path="/admin/agencies" element={<InviteAgency />} />
                <Route path="/admin/pricing" element={<PricingPage />} />
                <Route path="/admin/api-keys" element={<ApiKeysAdminPage />} />
                <Route path="*" element={<Navigate to="/admin/agencies" replace />} />
              </>
            ) : isOutlet ? (
              <>
                <Route path="/dashboard" element={<Dashboard profile={profile} />} />
                <Route path="/verify" element={<VerifyPage mode={mode} />} />
                <Route path="/history" element={<VerificationHistoryPage />} />
                <Route path="/wallet" element={<Wallet />} />
                <Route path="*" element={<Navigate to="/dashboard" replace />} />
              </>
            ) : (
              <>
                <Route path="/dashboard" element={<Dashboard profile={profile} />} />
                <Route path="/setup" element={<OrganizationSetupPage />} />
                <Route path="/verify" element={<VerifyPage mode={mode} />} />
                <Route path="/history" element={<VerificationHistoryPage />} />
                <Route path="/team" element={<TeamPage profile={profile} />} />
                <Route path="/outlets" element={<Outlets />} />
                <Route path="/outlets/:outletId" element={<OutletDetailPage />} />
                <Route path="/api-keys" element={<ApiKeysPage profile={profile} />} />
                <Route path="/wallet" element={<Wallet />} />
                <Route path="*" element={<Navigate to="/dashboard" replace />} />
              </>
            )}
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
                  <td>
                    <Link className="text-link" to={`/outlets/${String(item.id)}`}>
                      {String(item.name)}
                    </Link>
                  </td>
                  <td><span className={`badge ${String(item.status)}`}>{String(item.status)}</span></td>
                  <td><code>{String(item.walletId).slice(0, 8)}</code></td>
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
const BANK_ACCOUNTS = [
  { bank: "Zenith Bank", name: "Slogani Consults Nigeria Limited", number: "1017167544" },
  { bank: "Providus Bank", name: "Slogani Consults Nigeria Limited", number: "1310418112" },
];
const SUPPORT_WHATSAPP = (import.meta.env.VITE_SUPPORT_WHATSAPP as string | undefined)?.trim();
function Wallet() {
  const [balance, setBalance] = useState<Json>({});
  const [ledger, setLedger] = useState<Json[]>([]);
  const [params, setParams] = useSearchParams();
  const environment = useEnvironment();
  const [funding, setFunding] = useState(false);
  const [paying, setPaying] = useState(false);
  const [amount, setAmount] = useState(100000);
  const [fundMessage, setFundMessage] = useState<{ text: string; error?: boolean } | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  function load() {
    setLoadError(null);
    Promise.all([
      api.get<Json>("/v1/tenant/wallet/balance"),
      api.get<Json[]>("/v1/tenant/wallet/ledger?page=1&pageSize=20"),
    ])
      .then(([wallet, entries]) => {
        setBalance(wallet);
        setLedger(entries);
      })
      .catch((error) =>
        setLoadError(error instanceof Error ? error.message : "Wallet could not be loaded."),
      );
  }
  useEffect(load, []);
  // Flutterwave sends the payer back to /wallet?tx_ref=…&transaction_id=…&status=…
  useEffect(() => {
    const txRef = params.get("tx_ref") ?? params.get("txref");
    const transactionId = params.get("transaction_id");
    if (!txRef || !transactionId) return;
    setFundMessage({ text: "Confirming your payment…" });
    api
      .post<{ status?: string }>("/v1/tenant/wallet/topups/flutterwave/verify", {
        transactionReference: txRef,
        providerTransactionId: transactionId,
      })
      .then((result) => {
        setFundMessage({
          text:
            result?.status === "already_credited"
              ? "This payment was already credited."
              : "Payment confirmed — your wallet has been credited.",
        });
        load();
      })
      .catch((error) =>
        setFundMessage({
          text:
            error instanceof Error
              ? error.message
              : "We could not confirm the payment. If you were debited, contact support.",
          error: true,
        }),
      )
      .finally(() => {
        const next = new URLSearchParams(params);
        ["tx_ref", "txref", "transaction_id", "status"].forEach((key) => next.delete(key));
        setParams(next, { replace: true });
      });
    // Runs once for the redirect back from Flutterwave; params are read from the URL.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
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
  async function startFlutterwave() {
    if (amount < 50000) {
      setFundMessage({ text: "The minimum wallet funding is ₦50,000.", error: true });
      return;
    }
    setPaying(true);
    setFundMessage(null);
    try {
      const checkout = await api.post<{ checkoutUrl: string }>(
        "/v1/tenant/wallet/topups/flutterwave/initialize",
        { amountNaira: amount, redirectUrl: `${window.location.origin}/wallet` },
      );
      if (!checkout?.checkoutUrl) throw new Error("Flutterwave did not return a checkout link.");
      window.location.assign(checkout.checkoutUrl);
    } catch (error) {
      setFundMessage({ text: error instanceof Error ? error.message : "We could not start Flutterwave checkout.", error: true });
      setPaying(false);
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
      {environment !== "sandbox" && (
        <div className="section-heading">
          <div>
            <div className="eyebrow">ADD FUNDS</div>
            <h2>Top up your wallet</h2>
          </div>
        </div>
      )}
      {environment !== "sandbox" && (
        <div style={{ maxWidth: 420, marginTop: 8 }}>
          <label className="field">
            <span>Amount (₦)</span>
            <input type="number" min={50000} step={10000} value={amount}
              onChange={(event) => setAmount(Number(event.target.value))} />
          </label>
          <span className="field-hint">Minimum funding is ₦50,000.</span>
          <button className="button button-primary" style={{ marginTop: 12, width: "100%" }}
            onClick={() => void startFlutterwave()} disabled={paying || amount < 50000}>
            {paying ? <><span className="spinner" aria-hidden="true" />Starting checkout…</> : "Pay securely with Flutterwave ↗"}
          </button>
          <span className="field-hint" style={{ display: "block", marginTop: 10 }}>You'll be redirected to Flutterwave's secure checkout.</span>
        </div>
      )}
      {environment !== "sandbox" && (
        <>
          <div className="section-heading">
            <div>
              <div className="eyebrow">BANK TRANSFER</div>
              <h2>Or pay by transfer</h2>
            </div>
          </div>
          <p className="lede">
            Transfer the exact amount to either account, then send your receipt to support.
            Your wallet is credited once the payment is confirmed — usually within one business day.
          </p>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Bank</th>
                  <th>Account name</th>
                  <th>Account number</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {BANK_ACCOUNTS.map((account) => (
                  <tr key={account.number}>
                    <td>{account.bank}</td>
                    <td>{account.name}</td>
                    <td><code>{account.number}</code></td>
                    <td><CopyButton value={account.number} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="field-hint" style={{ display: "block", marginTop: 10 }}>
            {SUPPORT_WHATSAPP ? (
              <>
                Send your receipt to{" "}
                <a className="text-link" href={`https://wa.me/${SUPPORT_WHATSAPP.replace(/[^0-9]/g, "")}`} target="_blank" rel="noreferrer">
                  support on WhatsApp
                </a>{" "}
                so we can credit your wallet.
              </>
            ) : (
              "Send your payment receipt to TruvoID support so we can credit your wallet."
            )}
          </p>
        </>
      )}
      {fundMessage && (
        <div className={`notice ${fundMessage.error ? "error" : "success"}`} role="status">{fundMessage.text}</div>
      )}
      {loadError && (
        <div className="notice error" role="alert">
          {loadError} <button className="retry-button" onClick={() => load()}>Retry</button>
        </div>
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
  const [failed, setFailed] = useState(false);
  const [sending, setSending] = useState(false);
  const [organizations, setOrganizations] = useState<Json[]>([]);
  const [refresh, setRefresh] = useState(0);
  const [reviewing, setReviewing] = useState<{ id: string; name: string } | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [statusBusy, setStatusBusy] = useState<string | null>(null);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [invitations, setInvitations] = useState<Json[]>([]);
  const [invForm, setInvForm] = useState({ organizationType: "institution", organizationName: "", adminFullName: "", adminEmail: "" });
  const [invSending, setInvSending] = useState(false);
  const [invMessage, setInvMessage] = useState("");
  const [invFailed, setInvFailed] = useState(false);
  const [invLink, setInvLink] = useState("");
  const [invBusy, setInvBusy] = useState<string | null>(null);
  const [crediting, setCrediting] = useState<{ id: string; name: string } | null>(null);
  const [creditForm, setCreditForm] = useState({ amount: "", reference: "" });
  const [creditBusy, setCreditBusy] = useState(false);
  const [creditMessage, setCreditMessage] = useState<{ text: string; error?: boolean } | null>(null);
  useEffect(() => {
    setLoadError(null);
    api
      .get<Json[]>("/v1/admin/organizations")
      .then(setOrganizations)
      .catch((error) =>
        setLoadError(error instanceof Error ? error.message : "Organizations could not be loaded."),
      );
    api.get<Json[]>("/v1/admin/invitations").then(setInvitations).catch(() => undefined);
  }, [refresh]);

  async function submitInvitation(event: FormEvent) {
    event.preventDefault();
    if (invSending) return;
    setInvSending(true);
    setInvMessage("");
    setInvLink("");
    try {
      const result = await api.post<Json>("/v1/admin/invitations", invForm);
      setInvFailed(false);
      setInvLink(String(result.link ?? ""));
      setInvMessage(String(result.message ?? "Invitation created."));
      setInvForm({ ...invForm, organizationName: "", adminFullName: "", adminEmail: "" });
      setRefresh((value) => value + 1);
    } catch (error) {
      setInvFailed(true);
      setInvMessage(error instanceof Error ? error.message : "Could not create the invitation.");
    } finally {
      setInvSending(false);
    }
  }

  async function invitationAction(id: string, action: "resend" | "cancel") {
    if (invBusy) return;
    setInvBusy(id);
    setInvMessage("");
    setInvFailed(false);
    try {
      const result = await api.post<Json>(`/v1/admin/invitations/${id}/${action}`, {});
      if (action === "resend") setInvLink(String(result.link ?? ""));
      setInvMessage(String(result.message ?? (action === "resend" ? "New link issued." : "Invitation cancelled.")));
      setRefresh((value) => value + 1);
    } catch (error) {
      setInvFailed(true);
      setInvMessage(error instanceof Error ? error.message : "The invitation action failed.");
    } finally {
      setInvBusy(null);
    }
  }

  async function addCredit(event: FormEvent) {
    event.preventDefault();
    if (!crediting || creditBusy) return;
    const amountKobo = Math.round(Number(creditForm.amount) * 100);
    if (!creditForm.amount || Number.isNaN(amountKobo) || amountKobo <= 0) {
      setCreditMessage({ text: "Enter an amount greater than zero.", error: true });
      return;
    }
    setCreditBusy(true);
    setCreditMessage(null);
    try {
      const result = await api.post<Json>(`/v1/admin/tenant-wallets/${crediting.id}/credit`, {
        amountKobo,
        reference: creditForm.reference || null,
      });
      const naira = (kobo: number) => `₦${(kobo / 100).toLocaleString("en-NG", { minimumFractionDigits: 2 })}`;
      setCreditMessage({
        text: `Credited ${naira(amountKobo)} to ${crediting.name}. New balance: ${naira(Number(result.balanceAfterKobo ?? 0))}.`,
      });
      setCreditForm({ amount: "", reference: "" });
      setRefresh((value) => value + 1);
    } catch (error) {
      setCreditMessage({ text: error instanceof Error ? error.message : "Could not add credit.", error: true });
    } finally {
      setCreditBusy(false);
    }
  }
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (sending) return;
    setSending(true);
    setInvite("");
    setMessage("");
    try {
      const result = await api.post<Json>("/v1/admin/agencies/invite", form);
      setInvite(
        `${window.location.origin}/accept-agency-invite?token=${String(result.invitationToken)}`,
      );
      setFailed(false);
      setMessage(
        "Invitation created and emailed. You can also copy the link below and send it yourself.",
      );
      setForm({ agencyName: "", adminFullName: "", adminEmail: "" });
      setRefresh((value) => value + 1);
    } catch (error) {
      setFailed(true);
      setMessage(
        error instanceof Error ? error.message : "Could not create invitation.",
      );
    } finally {
      setSending(false);
    }
  }
  async function changeStatus(id: string, action: string) {
    if (statusBusy) return;
    setStatusBusy(id);
    setStatusError(null);
    try {
      await api.post(`/v1/admin/organizations/${id}/${action}`, {});
      setRefresh((value) => value + 1);
    } catch (error) {
      setStatusError(error instanceof Error ? error.message : "The status change failed.");
    } finally {
      setStatusBusy(null);
    }
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
          <Button disabled={sending}>{sending ? "Creating invitation…" : "Generate invitation ↗"}</Button>
        </form>
        <Notice message={message} error={failed} />
        {invite && (
          <div className="key-reveal">
            <span>Invitation link</span>
            <code>{invite}</code>
            <CopyButton value={invite} label="Copy invitation link" />
          </div>
        )}
      </div>
      {reviewing && (
        <AdminReview
          organization={reviewing}
          onClose={() => setReviewing(null)}
          onDecided={() => setRefresh((value) => value + 1)}
        />
      )}
      {crediting && (
        <div className="form-card narrow">
          <div className="eyebrow">ADD CREDIT — {crediting.name}</div>
          <form onSubmit={addCredit} noValidate>
            <label className="field"><span>Amount (₦)</span>
              <input inputMode="decimal" value={creditForm.amount}
                onChange={(event) => setCreditForm({ ...creditForm, amount: event.target.value })} placeholder="e.g. 50000" /></label>
            <label className="field"><span>Reference (optional)</span>
              <input value={creditForm.reference}
                onChange={(event) => setCreditForm({ ...creditForm, reference: event.target.value })} placeholder="Bank transfer ref / note" /></label>
            <div className="setup-actions">
              <button className="button button-primary" disabled={creditBusy}>{creditBusy ? "Crediting…" : "Add credit ↗"}</button>
              <button type="button" className="link-button" onClick={() => { setCrediting(null); setCreditMessage(null); }}>Cancel</button>
            </div>
          </form>
          {creditMessage && <div className={`notice ${creditMessage.error ? "error" : "success"}`} role="status">{creditMessage.text}</div>}
        </div>
      )}
      {statusError && (
        <div className="notice error" role="alert">{statusError}</div>
      )}
      {loadError ? (
        <div className="notice error" role="alert">
          {loadError}{" "}
          <button className="retry-button" onClick={() => setRefresh((value) => value + 1)}>
            Retry
          </button>
        </div>
      ) : organizations.length ? (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Organization</th>
                <th>Type</th>
                <th>Status</th>
                <th>Setup</th>
                <th>Users</th>
                <th>Balance</th>
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
                  <td>
                    <span className={`badge ${item.setupStatus === "approved" ? "active" : item.setupStatus === "submitted" ? "pending" : item.setupStatus === "needs_changes" ? "failed" : ""}`}>
                      {String(item.setupStatus).replace("_", " ")}
                    </span>
                    {item.type === "institution" && (
                      <button
                        className="link-button"
                        onClick={() => setReviewing({ id: String(item.id), name: String(item.name) })}
                      >
                        {item.setupStatus === "submitted" ? " Review ↗" : " View"}
                      </button>
                    )}
                  </td>
                  <td>{String(item.userCount)}</td>
                  <td>
                    {item.balanceKobo == null
                      ? <span className="muted">—</span>
                      : `₦${(Number(item.balanceKobo) / 100).toLocaleString("en-NG", { minimumFractionDigits: 2 })}`}
                  </td>
                  <td>
                    <button
                      className="link-button"
                      onClick={() => {
                        setCrediting({ id: String(item.id), name: String(item.name) });
                        setCreditMessage(null);
                        setCreditForm({ amount: "", reference: "" });
                      }}
                    >
                      Add credit
                    </button>{" "}
                    {String(item.status) === "suspended" ? (
                      <button
                        className="link-button"
                        disabled={statusBusy === String(item.id)}
                        onClick={() =>
                          void changeStatus(String(item.id), "reactivate")
                        }
                      >
                        {statusBusy === String(item.id) ? "Working…" : "Reactivate"}
                      </button>
                    ) : (
                      <button
                        className="link-button"
                        disabled={statusBusy === String(item.id)}
                        onClick={() =>
                          void changeStatus(String(item.id), "suspend")
                        }
                      >
                        {statusBusy === String(item.id) ? "Working…" : "Suspend"}
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

      <div className="section-heading">
        <div>
          <div className="eyebrow">PLATFORM / INVITATIONS</div>
          <h2>Create an organization</h2>
        </div>
      </div>
      <p className="lede">
        The organization and its administrator are created only when the invitation is accepted.
        Send an invitation to a new Institution or Agency and share the link if the email doesn't arrive.
      </p>
      <div className="form-card narrow">
        <form onSubmit={submitInvitation} noValidate>
          <label className="field"><span>Organization type</span>
            <select value={invForm.organizationType}
              onChange={(event) => setInvForm({ ...invForm, organizationType: event.target.value })}>
              <option value="institution">Institution</option>
              <option value="agency">Agency</option>
            </select>
          </label>
          <Field label="Organization name" required value={invForm.organizationName}
            onChange={(event) => setInvForm({ ...invForm, organizationName: event.target.value })} />
          <Field label="Administrator name" required value={invForm.adminFullName}
            onChange={(event) => setInvForm({ ...invForm, adminFullName: event.target.value })} />
          <Field label="Administrator email" required type="email" value={invForm.adminEmail}
            onChange={(event) => setInvForm({ ...invForm, adminEmail: event.target.value })} />
          <Button disabled={invSending}>{invSending ? "Creating invitation…" : "Send invitation ↗"}</Button>
        </form>
        <Notice message={invMessage} error={invFailed} />
        {invLink && (
          <div className="key-reveal">
            <span>Invitation link</span>
            <code>{invLink}</code>
            <CopyButton value={invLink} label="Copy invitation link" />
          </div>
        )}
      </div>

      {invitations.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th>Organization</th><th>Type</th><th>Administrator</th><th>Status</th><th>Expires</th><th /></tr>
            </thead>
            <tbody>
              {invitations.map((item) => {
                const status = String(item.status);
                return (
                  <tr key={String(item.id)}>
                    <td>{String(item.organizationName)}</td>
                    <td>{String(item.organizationType)}</td>
                    <td>{String(item.adminEmail)}</td>
                    <td><span className={`badge ${status === "accepted" ? "active" : status === "pending" ? "pending" : "failed"}`}>{status}</span></td>
                    <td>{item.expiresAt ? new Date(String(item.expiresAt)).toLocaleDateString() : "—"}</td>
                    <td>
                      {status === "pending" && (
                        <>
                          <button className="link-button" disabled={invBusy === String(item.id)}
                            onClick={() => void invitationAction(String(item.id), "resend")}>
                            {invBusy === String(item.id) ? "Working…" : "Resend"}
                          </button>{" "}
                          <button className="link-button" disabled={invBusy === String(item.id)}
                            onClick={() => void invitationAction(String(item.id), "cancel")}>
                            Cancel
                          </button>
                        </>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
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
  useEffect(() => {
    // Fired by api.ts when a refresh fails: drop back to the sign-in screen.
    const expire = () => setProfile(null);
    window.addEventListener(SESSION_EXPIRED_EVENT, expire);
    return () => window.removeEventListener(SESSION_EXPIRED_EVENT, expire);
  }, []);
  useEffect(() => {
    // Setup status changes what the shell shows (test-mode banner, Live switch).
    const reload = () => void api.profile().then(setProfile).catch(() => undefined);
    window.addEventListener(PROFILE_CHANGED_EVENT, reload);
    return () => window.removeEventListener(PROFILE_CHANGED_EVENT, reload);
  }, []);
  if (loading)
    return (
      <div className="loading-screen">
        <img className="brand-logo" src="/TruvoID-logo.png" alt="TruvoID" width={44} height={44} />
        <span className="pulse" />
      </div>
    );
  if (profile)
    return (
      <Shell
        profile={profile}
        onLogout={() => {
          void api.logout();
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
      <Route path="/accept-invite" element={<AcceptInvite kind="organization" Frame={AuthFrame} />} />
      <Route path="/admin/login" element={<AdminLogin onLogin={setProfile} Frame={AuthFrame} />} />
      <Route path="/forgot-password" element={<ForgotPassword Frame={AuthFrame} />} />
      <Route path="/reset-password" element={<ResetPassword Frame={AuthFrame} />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
