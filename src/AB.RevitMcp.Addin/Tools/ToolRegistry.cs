using AB.RevitMcp.Addin.Bridge;

namespace AB.RevitMcp.Addin.Tools
{
    /// <summary>
    /// Binds every descriptor in the shared <c>ToolCatalog</c> to its Revit implementation.
    ///
    /// <see cref="ToolRouter.Register"/> throws if a name here is not in the catalog, and
    /// <c>revit_bridge_status</c> reports any catalog entry that has no handler - so the two lists
    /// cannot silently drift apart.
    /// </summary>
    public static class ToolRegistry
    {
        public static void RegisterAll(ToolRouter router)
        {
            // ---------------- READ ----------------
            router.Register("revit_get_model_info", ReadTools.GetModelInfo);
            router.Register("revit_get_model_health", ReadTools.GetModelHealth);
            router.Register("revit_list_categories", ReadTools.ListCategories);
            router.Register("revit_list_levels", ReadTools.ListLevels);
            router.Register("revit_list_worksets", ReadTools.ListWorksets);
            router.Register("revit_list_views", ReadTools.ListViews);
            router.Register("revit_get_active_view", ReadTools.GetActiveView);
            router.Register("revit_list_sheets", ReadTools.ListSheets);
            router.Register("revit_list_schedules", ReadTools.ListSchedules);
            router.Register("revit_list_warnings", ReadTools.ListWarnings);
            router.Register("revit_list_linked_models", ReadTools.ListLinkedModels);
            router.Register("revit_list_materials", ReadTools.ListMaterials);
            router.Register("revit_list_family_types", ReadTools.ListFamilyTypes);
            router.Register("revit_list_rooms", ReadTools.ListRooms);
            router.Register("revit_list_grids", ReadTools.ListGrids);
            router.Register("revit_query_elements", ReadTools.QueryElements);
            router.Register("revit_search_elements", ReadTools.SearchElements);
            router.Register("revit_get_element_parameters", ReadTools.GetElementParameters);
            router.Register("revit_get_element_geometry", ReadTools.GetElementGeometry);
            router.Register("revit_get_selection", ReadTools.GetSelection);
            router.Register("revit_bridge_status", ReadTools.BridgeStatus);

            // ---------------- WRITE ----------------
            router.Register("revit_create_wall", WriteTools.CreateWall);
            router.Register("revit_create_column", WriteTools.CreateColumn);
            router.Register("revit_create_beam", WriteTools.CreateBeam);
            router.Register("revit_create_floor", WriteTools.CreateFloor);
            router.Register("revit_place_family_instance", WriteTools.PlaceFamilyInstance);
            router.Register("revit_create_door", WriteTools.CreateDoor);
            router.Register("revit_create_window", WriteTools.CreateWindow);
            router.Register("revit_create_level", WriteTools.CreateLevel);
            router.Register("revit_create_grid", WriteTools.CreateGrid);
            router.Register("revit_create_room", WriteTools.CreateRoom);
            router.Register("revit_set_parameters", WriteTools.SetParameters);
            router.Register("revit_move_elements", WriteTools.MoveElements);
            router.Register("revit_rotate_elements", WriteTools.RotateElements);
            router.Register("revit_copy_elements", WriteTools.CopyElements);
            router.Register("revit_create_view", WriteTools.CreateView);
            router.Register("revit_create_sheet", WriteTools.CreateSheet);
            router.Register("revit_place_view_on_sheet", WriteTools.PlaceViewOnSheet);
            router.Register("revit_set_element_material", WriteTools.SetElementMaterial);
            router.Register("revit_set_element_workset", WriteTools.SetElementWorkset);
            router.Register("revit_set_selection", WriteTools.SetSelection);

            // ---------------- MEP ----------------
            router.Register("revit_list_mep_systems", MepTools.ListMepSystems);
            router.Register("revit_create_duct", MepTools.CreateDuct);
            router.Register("revit_create_pipe", MepTools.CreatePipe);
            router.Register("revit_create_cable_tray", MepTools.CreateCableTray);
            router.Register("revit_create_conduit", MepTools.CreateConduit);
            router.Register("revit_get_view_center", ReadTools.GetViewCenter);

            // ---------------- ANNOTATION & DOCUMENTATION ----------------
            router.Register("revit_create_text_note", AnnotationTools.CreateTextNote);
            router.Register("revit_tag_elements", AnnotationTools.TagElements);
            router.Register("revit_create_detail_line", AnnotationTools.CreateDetailLine);
            router.Register("revit_create_schedule", AnnotationTools.CreateSchedule);

            // ---------------- VIEW MANAGEMENT & GRAPHICS ----------------
            router.Register("revit_duplicate_view", ViewTools.DuplicateView);
            router.Register("revit_apply_view_template", ViewTools.ApplyViewTemplate);
            router.Register("revit_set_element_visibility", ViewTools.SetElementVisibility);
            router.Register("revit_override_element_graphics", ViewTools.OverrideElementGraphics);
            router.Register("revit_set_view_section_box", ViewTools.SetViewSectionBox);

            // ---------------- GEOMETRY, FAMILIES & EXPORT ----------------
            router.Register("revit_mirror_elements", GeometryTools.MirrorElements);
            router.Register("revit_array_elements", GeometryTools.ArrayElements);
            router.Register("revit_group_elements", GeometryTools.GroupElements);
            router.Register("revit_join_geometry", GeometryTools.JoinGeometry);
            router.Register("revit_create_reference_plane", GeometryTools.CreateReferencePlane);
            router.Register("revit_load_family", GeometryTools.LoadFamily);
            router.Register("revit_create_ceiling", GeometryTools.CreateCeiling);
            router.Register("revit_export", GeometryTools.Export);

            // ---------------- MODIFY TAB ----------------
            router.Register("revit_split_element", ModifyTools.SplitElement);
            router.Register("revit_trim_extend_elements", ModifyTools.TrimExtendElements);
            router.Register("revit_align_elements", ModifyTools.AlignElements);
            router.Register("revit_offset_elements", ModifyTools.OffsetElements);
            router.Register("revit_pin_elements", ModifyTools.PinElements);
            router.Register("revit_cut_geometry", ModifyTools.CutGeometry);
            router.Register("revit_list_phases", ModifyTools.ListPhases);
            router.Register("revit_set_element_phase", ModifyTools.SetElementPhase);

            // ---------------- DESTRUCTIVE ----------------
            router.Register("revit_delete_elements", DestructiveTools.DeleteElements);
            router.Register("revit_purge_unused", DestructiveTools.PurgeUnused);
            router.Register("revit_unload_links", DestructiveTools.UnloadLinks);
            router.Register("revit_remove_links", DestructiveTools.RemoveLinks);
            router.Register("revit_delete_views", DestructiveTools.DeleteViews);

            // ---------------- ESCAPE HATCH (triple-gated) ----------------
            router.Register("revit_execute_code", CodeTools.ExecuteCode);
        }
    }
}
