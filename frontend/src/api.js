const BASE = (window.__API_URL__ || import.meta.env.VITE_API_URL || "http://localhost:8080").replace(/\/$/, "");

async function call(method, path, { studentId, body } = {}) {
  const headers = {};
  if (body) headers["Content-Type"] = "application/json";
  if (studentId) headers["X-Student-Id"] = studentId;
  let res;
  try {
    res = await fetch(BASE + path, { method, headers, body: body ? JSON.stringify(body) : undefined });
  } catch {
    throw new Error("সার্ভারের সাথে যোগাযোগ করা যাচ্ছে না। ইন্টারনেট সংযোগ দেখে একটু পরে আবার চেষ্টা করুন।");
  }
  if (res.status === 204) return null;
  let data = null;
  try { data = await res.json(); } catch { /* not json */ }
  if (!res.ok) throw new Error(data?.error || `কিছু একটা ভুল হয়েছে (কোড ${res.status})। আবার চেষ্টা করুন।`);
  return data;
}

export const api = {
  classes: () => call("GET", "/classes"),
  createClass: (name, totalSeats) => call("POST", "/classes", { body: { name, totalSeats } }),
  apply: (classId, studentId) => call("POST", `/classes/${classId}/holds`, { studentId }),
  confirm: (holdId, studentId) => call("POST", `/holds/${holdId}/confirm`, { studentId }),
  cancel: (holdId, studentId) => call("DELETE", `/holds/${holdId}`, { studentId }),
  myHolds: (studentId) => call("GET", "/me/holds", { studentId }),
};
