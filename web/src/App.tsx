import type { ReactNode } from 'react'
import { createBrowserRouter, Link, RouterProvider } from 'react-router-dom'
import { AuthProvider } from './auth/AuthContext'
import { AnnouncementsPage } from './clinic/AnnouncementsPage'
import { ClinicHistoryPage } from './clinic/ClinicHistoryPage'
import { ClinicLayout } from './clinic/ClinicLayout'
import { QueuePage } from './clinic/QueuePage'
import { SchedulePage } from './clinic/SchedulePage'
import { ReportsPage } from './clinic/ReportsPage'
import { StaffPage } from './clinic/StaffPage'
import { Layout, RequireAuth } from './components/Layout'
import { AccountPage } from './pages/AccountPage'
import { AdminAnalyticsPage } from './pages/admin/AdminAnalyticsPage'
import { AdminCitiesPage } from './pages/admin/AdminCitiesPage'
import { AdminDoctorDetailPage } from './pages/admin/AdminDoctorDetailPage'
import { AdminDoctorsPage } from './pages/admin/AdminDoctorsPage'
import {
  AdminAuditPage, AdminNotificationsPage, AdminOverviewPage, AdminPatientsPage, AdminQueuesPage,
} from './pages/admin/AdminOversightPages'
import { AppointmentPage, AppointmentsPage } from './pages/AppointmentsPage'
import { BookPage } from './pages/BookPage'
import { DoctorProfilePage } from './pages/DoctorProfilePage'
import { DoctorRegistrationPage } from './pages/DoctorRegistrationPage'
import { FamilyPage } from './pages/FamilyPage'
import { HomePage } from './pages/HomePage'
import { NotificationsPage } from './pages/NotificationsPage'
import { SignInPage } from './pages/SignInPage'
import { VisitsPage } from './pages/VisitsPage'

const signedIn = (page: ReactNode) => <RequireAuth>{page}</RequireAuth>
const admin = (page: ReactNode) => <RequireAuth role="SuperAdmin">{page}</RequireAuth>

const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { index: true, element: <HomePage /> },
      { path: 'doctors/:code', element: <DoctorProfilePage /> },
      { path: 'doctors/:code/book', element: signedIn(<BookPage />) },
      { path: 'sign-in', element: <SignInPage /> },
      { path: 'appointments', element: signedIn(<AppointmentsPage />) },
      { path: 'appointments/:id', element: signedIn(<AppointmentPage />) },
      { path: 'visits', element: signedIn(<VisitsPage />) },
      { path: 'family', element: signedIn(<FamilyPage />) },
      { path: 'notifications', element: signedIn(<NotificationsPage />) },
      { path: 'account', element: signedIn(<AccountPage />) },
      { path: 'doctor/register', element: signedIn(<DoctorRegistrationPage />) },
      {
        path: 'clinic',
        element: signedIn(<ClinicLayout />),
        children: [
          { index: true, element: <QueuePage /> },
          { path: 'schedule', element: <SchedulePage /> },
          { path: 'announcements', element: <AnnouncementsPage /> },
          { path: 'history', element: <ClinicHistoryPage /> },
          { path: 'reports', element: <ReportsPage /> },
          { path: 'staff', element: <StaffPage /> },
        ],
      },
      { path: 'admin', element: admin(<AdminOverviewPage />) },
      { path: 'admin/doctors', element: admin(<AdminDoctorsPage />) },
      { path: 'admin/doctors/:id', element: admin(<AdminDoctorDetailPage />) },
      { path: 'admin/patients', element: admin(<AdminPatientsPage />) },
      { path: 'admin/queues', element: admin(<AdminQueuesPage />) },
      { path: 'admin/notifications', element: admin(<AdminNotificationsPage />) },
      { path: 'admin/analytics', element: admin(<AdminAnalyticsPage />) },
      { path: 'admin/audit', element: admin(<AdminAuditPage />) },
      { path: 'admin/cities', element: admin(<AdminCitiesPage />) },
      {
        path: '*',
        element: (
          <section className="panel">
            <title>Page not found · Smart Doctor Queue</title>
            <h1>Page not found</h1>
            <Link to="/" className="button">Go to doctor search</Link>
          </section>
        ),
      },
    ],
  },
])

export default function App() {
  return (
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>
  )
}
