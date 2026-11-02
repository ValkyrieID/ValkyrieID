import { Routes } from '@angular/router';
import { freshTokenGuard } from './auth/fresh-token.guard';

export const routes: Routes = [
  {
    path: '',
    canActivateChild: [freshTokenGuard],
    children: [
      {
        path: '',
        title: 'Dashboard',
        loadComponent: () => import('./pages/dashboard.page').then((m) => m.DashboardPage),
      },
      {
        path: 'users',
        title: 'Users',
        loadComponent: () => import('./pages/users/user-list.page').then((m) => m.UserListPage),
      },
      {
        path: 'users/new',
        title: 'New user',
        loadComponent: () => import('./pages/users/user-create.page').then((m) => m.UserCreatePage),
      },
      {
        path: 'users/:id',
        title: 'User',
        loadComponent: () => import('./pages/users/user-detail.page').then((m) => m.UserDetailPage),
      },
      {
        path: 'roles',
        title: 'Roles',
        data: { kind: 'roles' },
        loadComponent: () => import('./pages/named/named-list.page').then((m) => m.NamedListPage),
      },
      {
        path: 'roles/:id',
        title: 'Role',
        data: { kind: 'roles' },
        loadComponent: () =>
          import('./pages/named/named-detail.page').then((m) => m.NamedDetailPage),
      },
      {
        path: 'groups',
        title: 'Groups',
        data: { kind: 'groups' },
        loadComponent: () => import('./pages/named/named-list.page').then((m) => m.NamedListPage),
      },
      {
        path: 'groups/:id',
        title: 'Group',
        data: { kind: 'groups' },
        loadComponent: () =>
          import('./pages/named/named-detail.page').then((m) => m.NamedDetailPage),
      },
      {
        path: 'clients',
        title: 'Clients',
        loadComponent: () =>
          import('./pages/clients/client-list.page').then((m) => m.ClientListPage),
      },
      {
        path: 'clients/new',
        title: 'New client',
        loadComponent: () =>
          import('./pages/clients/client-create.page').then((m) => m.ClientCreatePage),
      },
      {
        path: 'clients/:id',
        title: 'Client',
        loadComponent: () =>
          import('./pages/clients/client-detail.page').then((m) => m.ClientDetailPage),
      },
      {
        path: 'resources',
        title: 'API resources',
        loadComponent: () =>
          import('./pages/resources/resource-list.page').then((m) => m.ResourceListPage),
      },
      {
        path: 'resources/new',
        title: 'New API resource',
        loadComponent: () =>
          import('./pages/resources/resource-create.page').then((m) => m.ResourceCreatePage),
      },
      {
        path: 'resources/:id',
        title: 'API resource',
        loadComponent: () =>
          import('./pages/resources/resource-detail.page').then((m) => m.ResourceDetailPage),
      },
      {
        path: 'signing-keys',
        title: 'Signing keys',
        loadComponent: () => import('./pages/signing-keys.page').then((m) => m.SigningKeysPage),
      },
      {
        path: 'audit',
        title: 'Audit log',
        loadComponent: () => import('./pages/audit-log.page').then((m) => m.AuditLogPage),
      },
      {
        path: '**',
        title: 'Not found',
        loadComponent: () => import('./pages/not-found.page').then((m) => m.NotFoundPage),
      },
    ],
  },
];
