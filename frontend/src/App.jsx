import { useCallback, useEffect, useState } from "react";
import { api } from "./api";

const ID_RE = /^[0-9]{17}$/;
const STATUS_BN = {
  HELD: "সিট ধরা আছে, ফি বাকি",
  CONFIRMED: "ভর্তি নিশ্চিত",
  EXPIRED: "মেয়াদ শেষ",
  CANCELLED: "বাতিল",
};

function HoldCard({ h, now, busy, onPay, onCancel }) {
  const exp = new Date(h.expiresAt).getTime();
  const status = h.status === "HELD" && exp <= now ? "EXPIRED" : h.status;
  const left = Math.max(0, Math.ceil((exp - now) / 1000));
  const mm = String(Math.floor(left / 60)).padStart(2, "0");
  const ss = String(left % 60).padStart(2, "0");
  const showButtons = status === "HELD" || status === "EXPIRED";
  return (
    <div className={`card status-${status}`}>
      <div className="card-title">
        <strong>{h.className || h.classId}</strong>
        <span className="badge">{STATUS_BN[status]}</span>
      </div>
      {status === "HELD" && <p>মেয়াদ শেষ হতে বাকি: <strong>{mm}:{ss}</strong></p>}
      {status === "EXPIRED" && <p>সময়ের মধ্যে ফি জমা না হওয়ায় সিটটি ছেড়ে দেওয়া হয়েছে।</p>}
      {showButtons && (
        <div className="row">
          <button disabled={status !== "HELD" || busy} onClick={() => onPay(h.holdId)}>ভর্তি ফি দিন</button>
          <button className="ghost" disabled={status !== "HELD" || busy} onClick={() => onCancel(h.holdId)}>বাতিল</button>
        </div>
      )}
    </div>
  );
}

export default function App() {
  const [studentId, setStudentId] = useState(localStorage.getItem("studentId") || "");
  const [input, setInput] = useState(studentId);
  const [tab, setTab] = useState("classes");
  const [classes, setClasses] = useState(null);
  const [holds, setHolds] = useState([]);
  const [pollError, setPollError] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [now, setNow] = useState(Date.now());
  const [newName, setNewName] = useState("");
  const [newSeats, setNewSeats] = useState("");

  const valid = ID_RE.test(studentId);

  const loadClasses = useCallback(async () => {
    try { setClasses(await api.classes()); setPollError(""); }
    catch (e) { setPollError(e.message); }
  }, []);

  const loadHolds = useCallback(async () => {
    if (!ID_RE.test(studentId)) { setHolds([]); return; }
    try { setHolds(await api.myHolds(studentId)); setPollError(""); }
    catch (e) { setPollError(e.message); }
  }, [studentId]);

  useEffect(() => { loadClasses(); loadHolds(); }, [loadClasses, loadHolds]);
  useEffect(() => {
    const t = setInterval(() => { loadClasses(); loadHolds(); }, 3000);
    return () => clearInterval(t);
  }, [loadClasses, loadHolds]);
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);

  function saveId(e) {
    e.preventDefault();
    const v = input.trim();
    setError(""); setNotice("");
    if (!ID_RE.test(v)) { setError("জন্ম নিবন্ধন নম্বর ঠিক ১৭টি ইংরেজি অঙ্কের হতে হবে (যেমন 20152691234567890)।"); return; }
    localStorage.setItem("studentId", v);
    setStudentId(v);
    setNotice("জন্ম নিবন্ধন নম্বর সংরক্ষণ করা হয়েছে।");
  }

  async function act(fn, okMsg) {
    setError(""); setNotice(""); setBusy(true);
    try { await fn(); if (okMsg) setNotice(okMsg); }
    catch (e) { setError(e.message); }
    setBusy(false);
    await Promise.all([loadClasses(), loadHolds()]);
  }

  function requireId() {
    if (valid) return true;
    setError("আগে উপরের ঘরে শিক্ষার্থীর ১৭ অঙ্কের জন্ম নিবন্ধন নম্বর দিয়ে \"সংরক্ষণ\" চাপুন।");
    return false;
  }

  const apply = (classId) => requireId() && act(async () => { await api.apply(classId, studentId); setTab("holds"); }, "আবেদন হয়েছে। সময় শেষ হওয়ার আগে ভর্তি ফি দিন।");
  const pay = (holdId) => act(() => api.confirm(holdId, studentId), "ভর্তি ফি জমা হয়েছে, ভর্তি নিশ্চিত।");
  const cancel = (holdId) => act(() => api.cancel(holdId, studentId), "আবেদন বাতিল করা হয়েছে।");

  function createClass(e) {
    e.preventDefault();
    const seats = Number(newSeats);
    if (!newName.trim()) { setError("ক্লাসের নাম লিখুন।"); return; }
    if (!Number.isInteger(seats) || seats < 1) { setError("সিট সংখ্যা ১ বা তার বেশি একটি পূর্ণ সংখ্যা হতে হবে।"); return; }
    act(async () => { await api.createClass(newName.trim(), seats); setNewName(""); setNewSeats(""); }, "নতুন ক্লাস তৈরি হয়েছে।");
  }

  return (
    <div className="wrap">
      <h1>স্কুল ভর্তি</h1>

      <form className="idbox" onSubmit={saveId}>
        <label htmlFor="sid">শিক্ষার্থীর জন্ম নিবন্ধন নম্বর (১৭ অঙ্ক)</label>
        <div className="row">
          <input id="sid" inputMode="numeric" maxLength={17} value={input} onChange={(e) => setInput(e.target.value)} placeholder="20152691234567890" />
          <button type="submit">সংরক্ষণ</button>
        </div>
      </form>

      {pollError && <div className="msg err" role="alert">{pollError}</div>}
      {error && <div className="msg err" role="alert">{error}</div>}
      {notice && <div className="msg ok" role="status">{notice}</div>}

      <nav>
        <button className={tab === "classes" ? "tab active" : "tab"} onClick={() => setTab("classes")}>ক্লাস তালিকা</button>
        <button className={tab === "holds" ? "tab active" : "tab"} onClick={() => setTab("holds")}>আমার আবেদন</button>
        <button className={tab === "admin" ? "tab active" : "tab"} onClick={() => setTab("admin")}>ক্লাস তৈরি (অ্যাডমিন)</button>
      </nav>

      {tab === "classes" && (
        <section>
          {classes === null && !pollError && <p>লোড হচ্ছে...</p>}
          {classes && classes.length === 0 && <p>এখনো কোনো ক্লাস নেই। "ক্লাস তৈরি" ট্যাব থেকে প্রথম ক্লাস যোগ করুন।</p>}
          {classes && classes.length > 0 && (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr><th>ক্লাস</th><th>মোট সিট</th><th>ধরা আছে</th><th>নিশ্চিত</th><th>খালি</th><th></th></tr>
                </thead>
                <tbody>
                  {classes.map((c) => (
                    <tr key={c.id}>
                      <td>{c.name}</td>
                      <td>{c.totalSeats}</td>
                      <td>{c.heldSeats}</td>
                      <td>{c.confirmedSeats}</td>
                      <td><strong>{c.availableSeats}</strong></td>
                      <td>
                        <button disabled={busy || c.availableSeats === 0} onClick={() => apply(c.id)}>
                          {c.availableSeats === 0 ? "সিট নেই" : "আবেদন করুন"}
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      )}

      {tab === "holds" && (
        <section>
          {!valid && <p>আপনার আবেদন দেখতে উপরে জন্ম নিবন্ধন নম্বর দিন।</p>}
          {valid && holds.length === 0 && <p>এই শিক্ষার্থীর কোনো আবেদন নেই। "ক্লাস তালিকা" থেকে আবেদন করুন।</p>}
          {holds.map((h) => <HoldCard key={h.holdId} h={h} now={now} busy={busy} onPay={pay} onCancel={cancel} />)}
        </section>
      )}

      {tab === "admin" && (
        <section>
          <form className="idbox" onSubmit={createClass}>
            <label htmlFor="cname">ক্লাসের নাম</label>
            <input id="cname" value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="Class 6 - Morning Shift" />
            <label htmlFor="cseats">সিট সংখ্যা</label>
            <input id="cseats" inputMode="numeric" value={newSeats} onChange={(e) => setNewSeats(e.target.value)} placeholder="120" />
            <div className="row"><button type="submit" disabled={busy}>ক্লাস তৈরি করুন</button></div>
          </form>
        </section>
      )}
    </div>
  );
}
