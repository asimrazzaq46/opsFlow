import { ChangeDetectionStrategy, Component } from '@angular/core';
import { WorkspaceLayout } from './layout/workspace-layout';

@Component({
  selector: 'app-root',
  imports: [WorkspaceLayout],
  template: '<app-workspace-layout />',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
