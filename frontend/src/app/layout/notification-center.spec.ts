import { notificationDetailKey } from './notification-center';

describe('notificationDetailKey', () => {
  it('translates SLA kinds as SLA categories, not ticket statuses', () => {
    expect(notificationDetailKey({ type: 'SlaWarning', detail: 'FirstResponse' })).toBe('notifications.slaKinds.FirstResponse');
    expect(notificationDetailKey({ type: 'SlaBreached', detail: 'Resolution' })).toBe('notifications.slaKinds.Resolution');
  });

  it('keeps ticket statuses for the existing types', () => {
    expect(notificationDetailKey({ type: 'TicketStatusChanged', detail: 'Resolved' })).toBe('tickets.status.Resolved');
  });
});
