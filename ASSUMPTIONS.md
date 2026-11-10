# Assumptions

- **Seat গোনা:** `held` = মেয়াদ না-পেরোনো HELD, `confirmed` = CONFIRMED। EXPIRED আর CANCELLED seat দখল করে না।
- **মেয়াদ:** Background job নেই। `expiresAt` এখনকার সময়ের আগে হলে hold কে EXPIRED ধরা হয় (পড়ার সময় হিসাব)। তাই কোনো API call ছাড়াই seat ফেরত আসে এবং app restart হলেও ঠিক থাকে।
- **Concurrency:** আবেদন, confirm, cancel প্রতিটি transaction-এ class row-এ `SELECT ... FOR UPDATE` lock নেয়। Lock database-এ থাকে, তাই একই DB-তে একাধিক backend process আলাদা port-এ চললেও নিয়ম ১ ঠিক থাকে (Bonus)।
- **Student ID:** ঠিক ১৭টি ইংরেজি অঙ্ক না হলে 400।
- **একজন শিক্ষার্থী, এক class:** সেই class-এ active HELD বা CONFIRMED থাকলে নতুন আবেদন 409। EXPIRED/CANCELLED হলে আবার আবেদন করা যায়। আলাদা class-এ একসাথে আবেদন করা যায়।
- **Status code:** class/hold না থাকলে 404; অন্যের hold confirm/cancel করতে চাইলে 403; মেয়াদ-শেষ, বাতিল, বা নিশ্চিত হয়ে যাওয়া hold-এ ভুল action করলে 409; seat না থাকলে 409।
- **Idempotent:** আগে থেকে CONFIRMED hold-এ confirm আবার এলে 200, count বাড়ে না। আগে থেকে CANCELLED hold-এ DELETE আবার এলে 204।
- **Class তৈরি:** নাম unique (বড়/ছোট হাতের অক্ষর ধরা হয় না), সিট ১ থেকে ১,০০,০০০। Admin-এর জন্য login নেই (brief অনুযায়ী)।
- **`GET /holds/{id}`:** header লাগে না। Response-এ বাড়তি `studentId` আছে।
- **Error format:** `{ "error": "বাংলা বার্তা" }`। UI এটাই দেখায়।
- **UI:** প্রতি ৩ সেকেন্ডে class ও আবেদন নতুন করে আনা হয়, প্রতি সেকেন্ডে countdown চলে। জন্ম নিবন্ধন নম্বর browser-এর localStorage-এ থাকে, তাই refresh বা browser বন্ধ করে খুললেও আবেদন দেখা যায়। Frontend-এর EXPIRED দেখানো browser-এর ঘড়ির উপর নির্ভর করে, তবে সিদ্ধান্ত সবসময় backend-এর।
- **Payment:** "ভর্তি ফি দিন" চাপলেই payment সফল ধরা হয়।
