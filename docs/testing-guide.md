# Smart Doctor – Manual testing guide

## Before you start

- Web app: http://localhost:5173
- API: http://localhost:5221 (health check: http://localhost:5221/health)
- Use **three separate browser windows**, because each window keeps one sign-in:
  - Window A: normal browser window (Super Admin)
  - Window B: private / incognito window (Doctor)
  - Window C: a second browser, e.g. Edge if A and B are Chrome (Patient)
- Use made-up mobile numbers. In development the one-time sign-in code is shown on the screen (no real SMS).
- Each mobile number can request 5 codes per hour. If you run out, use a different number.

---

## 1. Super Admin setup (Window A)

1. Sign in with mobile number **9000000001** and the code shown on screen.
2. In development there is no authenticator step. (Production asks for an authenticator app code after the phone code;
   see `Auth__AdminMfaRequired` in the README.)
3. You land on the **Admin overview**. Check that the menu shows: **Overview, Doctors, Patients, Appointments, Failures, Analytics, Audit, Cities**.

Where each role lands after signing in: Super Admin → Admin overview; verified doctor or receptionist → Clinic appointments;
doctor waiting for verification → registration status; patient → Appointments. If you were sent to sign in from a
protected page, you go back to that page instead.

---

## 2. Doctor registration (Window B)

1. Click **Register** (top right), then **I'm a doctor**. Or click **Join as a doctor** on the home page.
2. **Step 1 of 2 · Your account:** enter **9876500001**, then the code shown on screen, then your name, and accept the terms.
3. You go straight to **Step 2 of 2 · Professional details**. Fill in:
   - Specialty
   - Medical Registration ID, for example `MMC-12345`
   - City: **Nagpur (NGP)**
   - Clinic name and address
4. Accept the declaration and click **Submit**. You see "Submitted for verification".
   - If you sign out and sign in again now, you land on this registration status page.
5. Go to **Window A**: Admin → **Doctors**. The new doctor is waiting for verification.
   - Open the doctor and click **Verify**.
   - Note the Doctor ID that is issued, for example `NGP-Dr-0001`.
6. Back in **Window B**, sign out and sign in again with 9876500001. You now land on **Clinic → Appointments**.
   - The doctor's menu shows only doctor items: **Clinic** (or **Registration** before verification), **Alerts** and
     **Sign out**. Patient pages such as Appointments, Family or Account send the doctor back to the clinic.
   - A patient's menu (section 3) has no "Register as a doctor" link.
7. The Appointments page shows **Finish setting up your clinic**. Until hours are set, patients see "This doctor has not
   published clinic hours yet" on the doctor's profile and cannot book appointments.
8. Click **Set your weekly clinic hours** (or the **Schedule** tab):
   - Click **Add session**, choose today's weekday, for example 08:00–14:00, capacity 20. Use **Copy Monday to Tue–Sat**
     to repeat a day.
   - Click **Save weekly hours**. The checklist disappears and the profile now lists sessions.
   - On the same page, **Holidays and special hours** closes a date or sets different hours for one date.
9. **Notices & leave** tab: post an **Out of clinic**, **Leave**, **Holiday**, **Delayed opening** or other notice.
   It appears as a banner on the doctor's profile. Closing notices cancel and notify bookings in that period.
10. **Profile & QR** tab: copy the public profile link, or click **Print QR poster** for the front desk.

---

## 3. Patient booking (Window C)

1. Click **Register**, then **I'm a patient** (or **Register as a patient** on the home page).
   Enter **9876500002**, the code, and your name. You land on **Appointments**.
2. Go to **Family** and add a child (enter a date of birth and confirm you are the guardian).
3. **Find a doctor** lists all doctors straight away. Narrow it with a name, Doctor ID, city or specialty, or leave
   "All cities" / "All specialties" to see everyone. Open the profile and click **Book appointment**.
   - Choose today's **Morning** session.
   - Choose who the appointment is for: yourself or the child.
4. Open **Appointments**. You should see:
   - The token, for example `T-001`
   - Your position in the line (patients ahead of you)
   - The estimated waiting time

---

## 4. Running appointments (Window B, with Window C open beside it)

1. Go to Clinic → **Appointments**.
2. Add 3 walk-ins: `Walk A`, `Walk B`, `Walk C`.
3. Click **Call next**.
   - In Window C, the patient gets a "Your turn is near" or "It's your turn" alert.
   - Their position updates live, without refreshing.
4. Click **Complete**, then call the next patient.
5. **Emergency:** mark a waiting patient as an emergency and choose a reason. They move to the front of the list.
6. **Missed turn:** call a patient, wait out the 3-minute grace period, then mark them **Missed**.
   - After 5 more patients are completed (or as soon as nobody else is waiting) they are put back in the appointment list.

---

## 5. Receptionist (role check)

1. In Window B, go to Clinic → **Staff** and invite mobile **9876500003**.
2. Open a fresh window, sign in as **9876500003**, go to **Account** and accept the invitation.
3. Check that the receptionist:
   - **can** run Appointments
   - **cannot** save changes to the Schedule (it is refused)
   - has **no** Staff tab
4. On the Free plan, inviting a **second** staff member is refused.

---

## 6. Plans and reports

1. In Window B, go to Clinic → **Reports**. It says reports are part of the Pro plan.
2. In Window A, open the doctor's page (Admin → Doctors → the doctor), set the plan to **Pro** and save.
3. Refresh Reports in Window B. You now see totals, average wait and consultation times, and a by-day table.
4. Inviting a second staff member is now allowed.

---

## 7. The code-review fixes

- **Closing while a patient is being seen**
  - With a patient called or in consultation, post a **Clinic closure** notice on the **Notices & leave** tab.
  - Expected: refused with the patient's token; appointments stay open.

- **Removing one delay keeps the others**
  - Post two **Delayed opening** notices for today: 30 minutes and 45 minutes.
  - The patient's estimate grows by 45 minutes.
  - Remove the 45-minute one. Expected: the delay drops to 30, not 0.

- **Overlapping closures**
  - Close tomorrow from the **Schedule** page, and also post a **Leave** notice covering tomorrow.
  - Remove only one of them. Expected: tomorrow still cannot be booked.

- **Walk-in validation**
  - Add a walk-in marked as an emergency without choosing a reason.
  - Expected: refused, and no stray patient record is created (check the patient list).

- **Admin authenticator lockout**
  - Only applies when the authenticator step is on (production). It is off in development, so this is covered by the
    automated tests (`dotnet test`) rather than by hand.

---

## 8. Admin oversight (Window A)

- **Analytics:** bookings, share booked online, wait and consultation times.
- **Patients:** search for a patient and open their history. You must enter a reason, and the access then appears under **Audit**.
- **Appointments:** today's live appointment sessions.
- **Failures:** notifications that could not be delivered.

---

## Automated tests

Run from a terminal:

```
cd C:\Pravin\AppointmentSystem\.claude\worktrees\review-fixes
dotnet test
```

This runs 126 backend tests on a temporary database.

---

## If something stops working

- If sign-in, search or booking fails, the API has probably stopped. Start it in your own terminal:

  ```
  cd C:\Pravin\AppointmentSystem\.claude\worktrees\review-fixes
  dotnet run --project src/SmartQueue.Api --launch-profile http
  ```

- Push notifications work in Chrome when you allow notifications for the site.
