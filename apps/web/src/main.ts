import { provideHttpClient } from "@angular/common/http";
import { bootstrapApplication } from "@angular/platform-browser";
import {
  RouteReuseStrategy,
  provideRouter,
  withInMemoryScrolling,
} from "@angular/router";
import { AppComponent } from "./app.component";
import { OrganizationRouteReuseStrategy, routes } from "./app.routes";
import { APP_BASE_HREF, ViewportScroller } from "@angular/common";
import { WorkspaceViewportScroller } from "./workbench/workspace-viewport-scroller";

bootstrapApplication(AppComponent, {
  providers: [
    { provide: APP_BASE_HREF, useValue: "/" },
    { provide: ViewportScroller, useClass: WorkspaceViewportScroller },
    {
      provide: RouteReuseStrategy,
      useFactory: () => new OrganizationRouteReuseStrategy(),
    },
    provideHttpClient(),
    provideRouter(
      routes,
      withInMemoryScrolling({ scrollPositionRestoration: "enabled" }),
    ),
  ],
}).catch((error: unknown) => {
  console.error("braid could not start", error);
});
