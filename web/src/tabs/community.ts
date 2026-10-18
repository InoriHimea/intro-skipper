import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { htmlEl } from "../components/dom.ts";
import { inputField } from "../components/input-field.ts";

export const communityTab: Tab = {
    id: "community",
    label: "Community",
    render(container) {
        const enabled = () => configStore.get("EnableCommunityFallback") === true;

        container.append(
            htmlEl(
                "div",
                { className: "field-description" },
                "<p>Local analysis always runs first. Only an episode or movie it settles <b>without</b> an intro or credits segment is looked up in the community databases, one request per item. A community result never replaces a local or user segment.</p>" +
                    "<p>Community timestamps receive the same boundary adjustments as detected ones, so the Segment Offset Adjustment settings on the Detection tab apply to them.</p>" +
                    "<p>Lookups use the series' IMDb, TMDB and TVDB ids. Enabling or changing these settings re-checks intro and credits results once on the next scan.</p>",
            ),
            inputField({
                kind: "checkbox",
                id: "EnableCommunityFallback",
                label: "Fill missing intros and credits from community databases",
                description: "Ask the enabled databases below for items local analysis found nothing for.",
            }),
            inputField({
                kind: "checkbox",
                id: "CommunityUseIntroDb",
                label: "IntroDB (introdb.app)",
                description: "Anonymous lookups by IMDb id. Series in a non-aired display order are skipped.",
                visible: enabled,
            }),
            inputField({
                kind: "checkbox",
                id: "CommunityUseTheIntroDb",
                label: "TheIntroDB (theintrodb.org)",
                description:
                    "Anonymous lookups by TVDB, TMDB or IMDb id, limited by the service to 5000 per day. Asked after IntroDB.",
                visible: enabled,
            }),
            inputField({
                kind: "number",
                id: "CommunityDurationToleranceSeconds",
                label: "Release mismatch tolerance (seconds)",
                min: 0,
                description:
                    "Timestamps are ignored when they reach this far past the end of the local file, or were measured on a release whose length differs by more than this.",
                visible: enabled,
            }),
        );
    },
};
